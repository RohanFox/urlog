// Sets the theme before the first paint: the user's stored choice, else the system's setting.
// A file of its own, so the page needs no inline script. src/app/theme.ts holds the same rule.
(function () {
  var choice = null;
  try {
    choice = window.localStorage.getItem('urlog.theme');
  } catch {
    choice = null;
  }
  if (choice !== 'light' && choice !== 'dark') {
    choice = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  document.documentElement.setAttribute('data-theme', choice);
})();
