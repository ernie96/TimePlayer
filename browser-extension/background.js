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

// First straight to TimePlayer (native messaging: no question from the browser). If TimePlayer can't be
// reached that way (an older TimePlayer, or links turned off), a timeplayer:// address does it through
// Windows, and the browser asks once whether to open TimePlayer. The page itself stays where it is.
function open(tab, href) {
  if (!tab || !href) return;
  if (!TimePlayerLinks.isYouTube(href)) {
    chrome.action.setBadgeText({ tabId: tab.id, text: "?" });
    chrome.action.setTitle({ tabId: tab.id, title: chrome.i18n.getMessage("notYouTube") });
    return;
  }
  chrome.runtime.sendNativeMessage("com.timeplayer.links", { url: href }, function (response) {
    if (chrome.runtime.lastError || !response || !response.ok)
      chrome.tabs.update(tab.id, { url: TimePlayerLinks.toTimePlayer(href) });
  });
}

// "Play in TimePlayer" links on pages ask the background to open them.
chrome.runtime.onMessage.addListener(function (message, sender) {
  if (message && message.play && sender.tab) open(sender.tab, message.play);
});

chrome.contextMenus.onClicked.addListener(function (info, tab) {
  open(tab, info.menuItemId === "play-link" ? info.linkUrl : (info.pageUrl || (tab && tab.url)));
});

chrome.action.onClicked.addListener(function (tab) { open(tab, tab.url); });

chrome.commands.onCommand.addListener(function (command, tab) {
  if (command !== "play-page") return;
  if (tab) { open(tab, tab.url); return; }
  chrome.tabs.query({ active: true, currentWindow: true }, function (tabs) { open(tabs[0], tabs[0] && tabs[0].url); });
});
