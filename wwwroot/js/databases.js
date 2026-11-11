(function () {
  var pathInput = document.getElementById("Input_FbPath");
  var osBtn = document.getElementById("dbBrowseOpen");
  var fileInput = document.getElementById("dbPathFile");
  var hintEl = document.getElementById("dbPathHint");
  var serverBtn = document.getElementById("dbBrowseServer");
  var modal = document.getElementById("dbBrowseModal");
  var listEl = document.getElementById("dbBrowseList");
  var crumbEl = document.getElementById("dbBrowsePath");
  var upBtn = document.getElementById("dbBrowseUp");
  var closeBtn = document.getElementById("dbBrowseClose");
  var cancelBtn = document.getElementById("dbBrowseCancel");
  var confirmBtn = document.getElementById("dbBrowseConfirm");
  var fileNameEl = document.getElementById("dbBrowseFileName");
  var errEl = document.getElementById("dbBrowseError");
  var matchModal = document.getElementById("dbMatchModal");
  var matchList = document.getElementById("dbMatchList");
  var matchClose = document.getElementById("dbMatchClose");
  var matchNote = document.getElementById("dbMatchNote");
  if (!pathInput) return;

  var parentPath = null;
  var selected = null;

  function setHint(msg) {
    if (!hintEl) return;
    hintEl.textContent = msg || "Use the pin to choose the company .FDB.";
    var isError = !!msg && (
      msg.indexOf("not on this server") >= 0
      || msg.indexOf("Could not") >= 0
      || msg.indexOf("More than one") >= 0
    );
    hintEl.classList.toggle("is-error", isError);
  }

  function applyPath(path) {
    pathInput.value = path;
    pathInput.dispatchEvent(new Event("input", { bubbles: true }));
    pathInput.dispatchEvent(new Event("change", { bubbles: true }));
    setHint("");
  }

  function openOsPicker() {
    if (!fileInput) return;
    fileInput.value = "";
    fileInput.click();
  }

  function showMatches(paths, note) {
    if (!matchModal || !matchList) {
      if (paths.length) applyPath(paths[0]);
      return;
    }
    if (matchNote) matchNote.textContent = note || "More than one file has that name. Pick the path to use.";
    matchList.innerHTML = "";
    paths.forEach(function (p) {
      var li = document.createElement("li");
      var btn = document.createElement("button");
      btn.type = "button";
      btn.className = "db-match-item";
      btn.textContent = p;
      btn.addEventListener("click", function () {
        applyPath(p);
        closeMatchModal();
      });
      li.appendChild(btn);
      matchList.appendChild(li);
    });
    matchModal.hidden = false;
    matchModal.setAttribute("aria-hidden", "false");
  }

  function closeMatchModal() {
    if (!matchModal) return;
    matchModal.hidden = true;
    matchModal.setAttribute("aria-hidden", "true");
  }

  function onOsPicked() {
    var file = fileInput && fileInput.files && fileInput.files[0];
    if (!file) return;
    setHint("Looking up “" + file.name + "” on this server…");
    var url = "/Databases?handler=Find&name=" + encodeURIComponent(file.name);
    fetch(url, { headers: { Accept: "application/json" }, credentials: "same-origin" })
      .then(function (res) {
        return res.json().then(function (data) {
          if (!res.ok) throw new Error((data && data.error) || "Could not look up that file.");
          return data;
        });
      })
      .then(function (data) {
        var paths = data.paths || [];
        if (paths.length === 1) {
          applyPath(paths[0]);
          return;
        }
        if (paths.length === 0) {
          setHint("That file is not on this server. Choose a company .FDB that exists here, or browse server folders.");
          return;
        }
        showMatches(paths, "More than one file has that name. Pick the path to use.");
      })
      .catch(function (err) {
        setHint(err.message || "Could not look up that file.");
      });
  }

  function setError(msg) {
    if (!errEl) return;
    errEl.textContent = msg || "";
    errEl.hidden = !msg;
  }

  function setSelected(entry) {
    selected = entry && entry.kind === "file" ? entry : null;
    if (fileNameEl) fileNameEl.value = selected ? selected.name : "";
    if (confirmBtn) confirmBtn.disabled = !selected;
    if (!listEl) return;
    Array.prototype.forEach.call(listEl.querySelectorAll("tr"), function (row) {
      row.classList.toggle("is-selected", !!(selected && row.getAttribute("data-path") === selected.path));
    });
  }

  function confirmSelection() {
    if (!selected) return;
    applyPath(selected.path);
    closeServerModal();
  }

  function openServerModal() {
    if (!modal || !listEl) return;
    modal.hidden = false;
    modal.setAttribute("aria-hidden", "false");
    setSelected(null);
    var start = (pathInput.value || "").trim();
    if (start && /[\\/]/.test(start)) {
      var slash = Math.max(start.lastIndexOf("\\"), start.lastIndexOf("/"));
      loadBrowse(slash > 0 ? start.slice(0, slash) : "");
    } else {
      loadBrowse("");
    }
  }

  function closeServerModal() {
    if (!modal) return;
    modal.hidden = true;
    modal.setAttribute("aria-hidden", "true");
    setError("");
    setSelected(null);
  }

  function formatSize(bytes) {
    if (bytes == null || bytes === "") return "";
    var n = Number(bytes);
    if (!isFinite(n) || n < 0) return "";
    if (n < 1024) return n + " B";
    if (n < 1024 * 1024) return (n / 1024).toFixed(0) + " KB";
    if (n < 1024 * 1024 * 1024) return (n / (1024 * 1024)).toFixed(1) + " MB";
    return (n / (1024 * 1024 * 1024)).toFixed(2) + " GB";
  }

  function formatDate(iso) {
    if (!iso) return "";
    var d = new Date(iso);
    if (isNaN(d.getTime())) return "";
    return d.toLocaleString(undefined, {
      year: "numeric",
      month: "numeric",
      day: "numeric",
      hour: "numeric",
      minute: "2-digit",
    });
  }

  function loadBrowse(path) {
    if (!listEl) return;
    setError("");
    setSelected(null);
    listEl.innerHTML = "<tr><td colspan=\"4\" class=\"db-browse-loading\">Loading…</td></tr>";
    var url = "/Databases?handler=Browse";
    if (path) url += "&path=" + encodeURIComponent(path);

    fetch(url, { headers: { Accept: "application/json" }, credentials: "same-origin" })
      .then(function (res) {
        return res.json().then(function (data) {
          if (!res.ok) throw new Error((data && data.error) || "Browse failed.");
          return data;
        });
      })
      .then(renderBrowse)
      .catch(function (err) {
        listEl.innerHTML = "";
        setError(err.message || "Browse failed.");
      });
  }

  function renderBrowse(data) {
    parentPath = data.parentPath || null;
    if (crumbEl) crumbEl.textContent = data.currentPath || "This PC";
    if (upBtn) upBtn.disabled = !data.currentPath && !parentPath;

    listEl.innerHTML = "";
    var entries = data.entries || [];
    if (!entries.length) {
      listEl.innerHTML = "<tr><td colspan=\"4\" class=\"db-browse-empty\">No folders or .FDB files here.</td></tr>";
      return;
    }

    entries.forEach(function (entry) {
      var tr = document.createElement("tr");
      tr.className = "db-browse-row db-browse-row--" + entry.kind;
      tr.setAttribute("data-path", entry.path);
      tr.tabIndex = 0;

      var nameTd = document.createElement("td");
      nameTd.className = "db-browse-col-name";
      nameTd.innerHTML =
        "<span class=\"db-browse-ico\" aria-hidden=\"true\"></span>" +
        "<span class=\"db-browse-name\"></span>";
      nameTd.querySelector(".db-browse-name").textContent = entry.name;

      var dateTd = document.createElement("td");
      dateTd.textContent = formatDate(entry.modifiedUtc);

      var typeTd = document.createElement("td");
      typeTd.textContent = entry.typeLabel || (entry.kind === "dir" ? "File folder" : "");

      var sizeTd = document.createElement("td");
      sizeTd.className = "db-browse-col-size";
      sizeTd.textContent = entry.kind === "file" ? formatSize(entry.sizeBytes) : "";

      tr.appendChild(nameTd);
      tr.appendChild(dateTd);
      tr.appendChild(typeTd);
      tr.appendChild(sizeTd);

      tr.addEventListener("click", function () {
        if (entry.kind === "file") {
          setSelected(entry);
          return;
        }
        loadBrowse(entry.path);
      });

      tr.addEventListener("dblclick", function () {
        if (entry.kind === "file") {
          setSelected(entry);
          confirmSelection();
          return;
        }
        loadBrowse(entry.path);
      });

      listEl.appendChild(tr);
    });
  }

  if (osBtn) osBtn.addEventListener("click", openOsPicker);
  if (fileInput) fileInput.addEventListener("change", onOsPicked);
  if (serverBtn) serverBtn.addEventListener("click", openServerModal);
  if (closeBtn) closeBtn.addEventListener("click", closeServerModal);
  if (cancelBtn) cancelBtn.addEventListener("click", closeServerModal);
  if (confirmBtn) confirmBtn.addEventListener("click", confirmSelection);
  if (matchClose) matchClose.addEventListener("click", closeMatchModal);
  if (upBtn) {
    upBtn.addEventListener("click", function () {
      if (parentPath) loadBrowse(parentPath);
      else loadBrowse("");
    });
  }
  if (modal) {
    modal.addEventListener("click", function (e) {
      if (e.target === modal) closeServerModal();
    });
  }
  if (matchModal) {
    matchModal.addEventListener("click", function (e) {
      if (e.target === matchModal) closeMatchModal();
    });
  }
  document.addEventListener("keydown", function (e) {
    if (e.key !== "Escape") return;
    if (matchModal && !matchModal.hidden) closeMatchModal();
    else if (modal && !modal.hidden) closeServerModal();
  });
})();
