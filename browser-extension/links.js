// Shared by the page script and the background worker: which links are YouTube videos or playlists,
// and the timeplayer:// link that hands one to TimePlayer.
var TimePlayerLinks = (function () {
  var hosts = /^(?:www\.|m\.|music\.)?(?:youtube\.com|youtube-nocookie\.com)$/i;

  function isYouTube(href) {
    var url;
    try { url = new URL(href); } catch (e) { return false; }
    if (url.protocol !== "https:" && url.protocol !== "http:") return false;
    if (/^(?:www\.)?youtu\.be$/i.test(url.hostname)) return /^\/[A-Za-z0-9_-]{11}/.test(url.pathname);
    if (!hosts.test(url.hostname)) return false;
    if (url.pathname === "/watch") return /^[A-Za-z0-9_-]{11}$/.test(url.searchParams.get("v") || "");
    if (url.pathname === "/playlist") return !!url.searchParams.get("list");
    return /^\/(?:shorts|live|embed|v)\/[A-Za-z0-9_-]{11}/.test(url.pathname);
  }

  function toTimePlayer(href) {
    return "timeplayer://open?url=" + encodeURIComponent(href);
  }

  return { isYouTube: isYouTube, toTimePlayer: toTimePlayer };
})();
