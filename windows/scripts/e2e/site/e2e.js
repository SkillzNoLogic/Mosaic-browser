// Shared by the end-to-end test pages: reports observations back to the test server
// (GET /report?token=...&key=value), which the scenarios wait for. The token comes from the
// page's query string, so one scenario's reports never mix with another's.
(function () {
  var params = new URLSearchParams(location.search);
  var token = params.get('token') || '';
  function report(key, value) {
    var url = '/report?token=' + encodeURIComponent(token) + '&' + encodeURIComponent(key) + '=' + encodeURIComponent(String(value));
    try { fetch(url, { cache: 'no-store', keepalive: true }); } catch (e) { new Image().src = url; }
  }
  function visible(element) {
    if (!element) return 'missing';
    var style = getComputedStyle(element);
    if (style.display === 'none' || style.visibility === 'hidden' || element.offsetHeight === 0) return 'hidden';
    return 'visible';
  }
  window.E2E = { token: token, params: params, report: report, visible: visible };
  report('loaded', location.pathname);
})();
