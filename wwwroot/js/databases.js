(function () {
  var pathInput = document.getElementById("Input_FbPath");
  var openBtn = document.getElementById("dbBrowseOpen");
  var modal = document.getElementById("dbBrowseModal");
  var listEl = document.getElementById("dbBrowseList");
  var crumbEl = document.getElementById("dbBrowsePath");
  var upBtn = document.getElementById("dbBrowseUp");
  var closeBtn = document.getElementById("dbBrowseClose");
  var cancelBtn = document.getElementById("dbBrowseCancel");
  var confirmBtn = document.getElementById("dbBrowseConfirm");
  var fileNameEl = document.getElementById("dbBrowseFileName");
  var errEl = document.getElementById("dbBrowseError");
  if (!pathInput || !openBtn || !modal || !listEl) return;

  var parentPath = null;
  var selected = null;

  function setError(msg) {
    if (!errEl) return;
    errEl.textContent = msg || "";
    errEl.hidden = !msg;
  }

  function setSelected(entry) {
    selected = entry && entry.kind === "file" ? entry : null;
    if (fileNameEl) fileNameEl.value = selected ? selected.name : "";
    if (confirmBtn) confirmBtn.disabled = !selected;
    Array.prototype.forEach.call(listEl.querySelectorAll("tr"), function (row) {
      row.classList.toggle("is-selected", !!(selected && row.getAttribute("data-path") === selected.path));
    });
  }

  function confirmSelection() {
    if (!selected) return;
    pathInput.value = selected.path;
    pathInput.dispatchEvent(new Event("input", { bubbles: true }));
    closeModal();
  }

  function openModal() {
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

  function closeModal() {
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

      tr.addEventListener("keydown", function (e) {
        if (e.key !== "Enter") return;
        if (entry.kind === "file") {
          setSelected(entry);
          confirmSelection();
        } else {
          loadBrowse(entry.path);
        }
      });

      listEl.appendChild(tr);
    });
  }

  openBtn.addEventListener("click", openModal);
  if (closeBtn) closeBtn.addEventListener("click", closeModal);
  if (cancelBtn) cancelBtn.addEventListener("click", closeModal);
  if (confirmBtn) confirmBtn.addEventListener("click", confirmSelection);
  if (upBtn) {
    upBtn.addEventListener("click", function () {
      if (parentPath) loadBrowse(parentPath);
      else loadBrowse("");
    });
  }
  modal.addEventListener("click", function (e) {
    if (e.target === modal) closeModal();
  });
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape" && !modal.hidden) closeModal();
  });
})();
