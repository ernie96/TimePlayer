importScripts("links.js");

var linkPatterns = [
  "*://*.youtube.com/watch*", "*://youtube.com/watch*",
  "*://*.youtube.com/playlist*", "*://youtube.com/playlist*",
  "*://*.youtube.com/shorts/*", "*://youtube.com/shorts/*",
  "*://*.youtube.com/live/*", "*://youtube.com/live/*",
  "*://youtu.be/*"
];

chrome.runtime.onInstalled.addListener(function () {
  chrome.contextMenus.removeAll(function () {
    chrome.contextMenus.create({
      id: "play-link", title: chrome.i18n.getMessage("playLink"),
      contexts: ["link"], targetUrlPatterns: linkPatterns
    });
    chrome.contextMenus.create({
      id: "play-page", title: chrome.i18n.getMessage("playPage"),
      contexts: ["page"], documentUrlPatterns: linkPatterns
    });
  });
});

// Opening a timeplayer:// address hands it to Windows, which starts TimePlayer (or passes the link to
// the open copy). The page itself stays where it is.
function open(tab, href) {
  if (!tab || !href) return;
  if (!TimePlayerLinks.isYouTube(href)) {
    chrome.action.setBadgeText({ tabId: tab.id, text: "?" });
    chrome.action.setTitle({ tabId: tab.id, title: chrome.i18n.getMessage("notYouTube") });
    return;
  }
  chrome.tabs.update(tab.id, { url: TimePlayerLinks.toTimePlayer(href) });
}

chrome.contextMenus.onClicked.addListener(function (info, tab) {
  open(tab, info.menuItemId === "play-link" ? info.linkUrl : (info.pageUrl || (tab && tab.url)));
});

chrome.action.onClicked.addListener(function (tab) { open(tab, tab.url); });

chrome.commands.onCommand.addListener(function (command, tab) {
  if (command !== "play-page") return;
  if (tab) { open(tab, tab.url); return; }
  chrome.tabs.query({ active: true, currentWindow: true }, function (tabs) { open(tabs[0], tabs[0] && tabs[0].url); });
});
