(function () {
  if (window.hljs) {
    hljs.configure({ ignoreUnescapedHTML: true });
    // 言語指定のあるコードブロックのみハイライト（指定なしはそのまま表示）
    document.querySelectorAll('pre code[class*="language-"]').forEach(function (el) {
      try { hljs.highlightElement(el); } catch (e) { /* 未対応言語は無視 */ }
    });
  }
})();
