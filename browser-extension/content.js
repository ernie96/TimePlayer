// Adds a "Play in TimePlayer" link after each YouTube link on a page (Reddit, email, forums...),
// or makes the YouTube links themselves open in TimePlayer, as chosen in the extension's options.
// YouTube's own pages are left alone: use the toolbar button, Alt+Shift+P or the context menu there.
(function () {
  var MARK = "timeplayerDone";
  var mode = "after";
  var label = chrome.i18n.getMessage("playInTimePlayer");

  function handle(link) {
    if (link.dataset[MARK] || link.classList.contains("timeplayer-link")) return;
    link.dataset[MARK] = "1";
    if (!TimePlayerLinks.isYouTube(link.href)) return;
    var target = TimePlayerLinks.toTimePlayer(link.href);
    if (mode === "replace") {
      link.href = target;
      link.removeAttribute("target");
      return;
    }
    var extra = document.createElement("a");
    extra.href = target;
    extra.className = "timeplayer-link";
    extra.textContent = " (" + label + ")";
    var name = (link.textContent || "").trim();
    if (name) extra.setAttribute("aria-label", label + ": " + name);
    link.insertAdjacentElement("afterend", extra);
  }

  function scan(root) {
    if (root.nodeType !== 1) return;
    if (root.tagName === "A" && root.href) handle(root);
    var links = root.querySelectorAll ? root.querySelectorAll("a[href]") : [];
    for (var i = 0; i < links.length; i++) handle(links[i]);
  }

  chrome.storage.sync.get({ mode: "after" }, function (settings) {
    mode = settings.mode;
    if (mode === "off") return;
    scan(document.body);
    // Pages that load more as you read (Reddit, chat, webmail).
    var pending = [];
    var timer = null;
    new MutationObserver(function (changes) {
      for (var i = 0; i < changes.length; i++)
        for (var j = 0; j < changes[i].addedNodes.length; j++) pending.push(changes[i].addedNodes[j]);
      if (timer) return;
      timer = setTimeout(function () {
        timer = null;
        var nodes = pending; pending = [];
        for (var k = 0; k < nodes.length; k++) scan(nodes[k]);
      }, 300);
    }).observe(document.body, { childList: true, subtree: true });
  });
})();
