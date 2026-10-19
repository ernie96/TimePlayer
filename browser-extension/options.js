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
