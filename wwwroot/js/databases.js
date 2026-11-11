(function () {
  var pathInput = document.getElementById("Input_FbPath");
  var openBtn = document.getElementById("dbBrowseOpen");
  var modal = document.getElementById("dbBrowseModal");
  var listEl = document.getElementById("dbBrowseList");
  var crumbEl = document.getElementById("dbBrowsePath");
  var upBtn = document.getElementById("dbBrowseUp");
  var closeBtn = document.getElementById("dbBrowseClose");
  var errEl = document.getElementById("dbBrowseError");
  if (!pathInput || !openBtn || !modal || !listEl) return;

  var parentPath = null;

  function setError(msg) {
    if (!errEl) return;
    errEl.textContent = msg || "";
    errEl.hidden = !msg;
  }

  function openModal() {
    modal.hidden = false;
    modal.setAttribute("aria-hidden", "false");
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
  }

  function loadBrowse(path) {
    setError("");
    listEl.innerHTML = "<li class=\"db-browse-loading\">Loading…</li>";
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
    if (crumbEl) crumbEl.textContent = data.currentPath || "This computer";
    if (upBtn) upBtn.disabled = !data.currentPath && !parentPath;

    listEl.innerHTML = "";
    var entries = data.entries || [];
    if (!entries.length) {
      listEl.innerHTML = "<li class=\"db-browse-empty\">No folders or .FDB files here.</li>";
      return;
    }

    entries.forEach(function (entry) {
      var li = document.createElement("li");
      var btn = document.createElement("button");
      btn.type = "button";
      btn.className = "db-browse-item db-browse-item--" + entry.kind;
      btn.innerHTML =
        "<span class=\"db-browse-ico\" aria-hidden=\"true\"></span>" +
        "<span class=\"db-browse-name\"></span>";
      btn.querySelector(".db-browse-name").textContent = entry.name;
      btn.addEventListener("click", function () {
        if (entry.kind === "file") {
          pathInput.value = entry.path;
          pathInput.dispatchEvent(new Event("input", { bubbles: true }));
          closeModal();
          return;
        }
        loadBrowse(entry.path);
      });
      li.appendChild(btn);
      listEl.appendChild(li);
    });
  }

  openBtn.addEventListener("click", openModal);
  if (closeBtn) closeBtn.addEventListener("click", closeModal);
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
