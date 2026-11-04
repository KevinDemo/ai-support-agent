// The System / Light / Dark switch, same behaviour and same localStorage key as the homepage.
(function () {
  var root = document.documentElement;
  var metas = document.querySelectorAll('meta[name="theme-color"]');
  var system = [].map.call(metas, function (m) { return m.content; });
  var chrome = { light: "#f6f6f4", dark: "#0a0b0c" };
  var radios = document.querySelectorAll('input[name="theme"]');

  function apply(choice) {
    if (choice === "system") delete root.dataset.theme;
    else root.dataset.theme = choice;
    [].forEach.call(metas, function (m, i) { m.content = chrome[choice] || system[i]; });
  }

  var saved = "system";
  try { saved = localStorage.getItem("theme") || "system"; } catch (e) {}
  if (saved !== "light" && saved !== "dark") saved = "system";
  apply(saved);

  [].forEach.call(radios, function (radio) {
    radio.checked = radio.value === saved;
    radio.addEventListener("change", function () {
      apply(radio.value);
      try { localStorage.setItem("theme", radio.value); } catch (e) {}
    });
  });
})();
