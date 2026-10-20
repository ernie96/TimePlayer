document.getElementById("legend").textContent = chrome.i18n.getMessage("modeLegend");
document.querySelectorAll("[data-msg]").forEach(function (el) { el.textContent = chrome.i18n.getMessage(el.dataset.msg); });

chrome.storage.sync.get({ mode: "after" }, function (settings) {
  var choice = document.querySelector('input[value="' + settings.mode + '"]');
  if (choice) choice.checked = true;
});

document.querySelectorAll('input[name="mode"]').forEach(function (input) {
  input.addEventListener("change", function () {
    chrome.storage.sync.set({ mode: input.value }, function () {
      document.getElementById("status").textContent = chrome.i18n.getMessage("saved");
    });
  });
});

// Optional: export the YouTube sign-in so TimePlayer can use the account (Settings, YouTube account).
// The permissions are asked for only now, and given back right after.
document.getElementById("signinHeading").textContent = chrome.i18n.getMessage("signinHeading");
var exportButton = document.getElementById("exportSignin");
exportButton.textContent = chrome.i18n.getMessage("exportSignin");
exportButton.addEventListener("click", function () {
  var status = document.getElementById("signinStatus");
  var wanted = { permissions: ["cookies", "downloads"], origins: ["https://*.youtube.com/*", "https://*.google.com/*"] };
  chrome.permissions.request(wanted, function (granted) {
    if (!granted) { status.textContent = chrome.i18n.getMessage("signinDenied"); return; }
    Promise.all([
      chrome.cookies.getAll({ domain: "youtube.com" }),
      chrome.cookies.getAll({ domain: "google.com" })
    ]).then(function (lists) {
      var cookies = lists[0].concat(lists[1]);
      if (!cookies.some(function (c) { return c.name === "SAPISID" || c.name === "__Secure-3PAPISID"; })) {
        status.textContent = chrome.i18n.getMessage("signinNotSignedIn");
        chrome.permissions.remove(wanted);
        return;
      }
      var lines = ["# Netscape HTTP Cookie File"];
      cookies.forEach(function (c) {
        var domain = c.hostOnly ? c.domain.replace(/^\./, "") : (c.domain.charAt(0) === "." ? c.domain : "." + c.domain);
        lines.push([c.httpOnly ? "#HttpOnly_" + domain : domain, c.hostOnly ? "FALSE" : "TRUE", c.path,
          c.secure ? "TRUE" : "FALSE", Math.floor(c.expirationDate || 0), c.name, c.value].join("\t"));
      });
      var url = URL.createObjectURL(new Blob([lines.join("\n") + "\n"], { type: "text/plain" }));
      chrome.downloads.download({ url: url, filename: "timeplayer-youtube-signin.txt", conflictAction: "overwrite", saveAs: false }, function () {
        setTimeout(function () { URL.revokeObjectURL(url); chrome.permissions.remove(wanted); }, 5000);
        status.textContent = chrome.i18n.getMessage("signinSaved");
      });
    });
  });
});
