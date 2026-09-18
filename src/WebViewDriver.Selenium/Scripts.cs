using OpenQA.Selenium;

namespace WebViewDriver.Selenium;

/// <summary>
/// Command scripts executed through the in-page bridge. Each constant is a
/// JavaScript function body (W3C executeScript style); element and shadow-root
/// arguments arrive already dereferenced by the bridge, and the body runs in
/// the current browsing context's realm. Element commands resolve document and
/// window through the element itself (el.ownerDocument.defaultView) so elements
/// living in same-origin frames behave correctly regardless of context.
/// </summary>
internal static class Scripts
{
    /// <summary>args: strategy ('css'|'xpath'), selector, root (element/shadow root or null), all (bool).</summary>
    public const string Find = """
        var strategy = arguments[0], selector = arguments[1], root = arguments[2], all = arguments[3];
        var ctx = root || document;
        if (strategy !== 'css' && root && root.nodeType === 11) {
          var unsupported = new Error('XPath cannot be used inside a shadow root; use a CSS selector.');
          unsupported.__wvd = 'invalid selector';
          throw unsupported;
        }
        var results = [];
        try {
          if (strategy === 'css') {
            if (all) {
              results = Array.prototype.slice.call(ctx.querySelectorAll(selector));
            } else {
              var single = ctx.querySelector(selector);
              if (single) { results.push(single); }
            }
          } else {
            var doc = ctx.nodeType === 9 ? ctx : ctx.ownerDocument;
            var snapshot = doc.evaluate(selector, ctx, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
            for (var i = 0; i < snapshot.snapshotLength; i++) {
              var node = snapshot.snapshotItem(i);
              if (node.nodeType === 1) {
                results.push(node);
                if (!all) { break; }
              }
            }
          }
        } catch (e) {
          var err = new Error('Invalid selector "' + selector + '": ' + (e.message || e));
          err.__wvd = 'invalid selector';
          throw err;
        }
        return all ? results : (results.length ? results[0] : null);
        """;

    public const string Click = """
        var el = arguments[0];
        var doc = el.ownerDocument;
        var win = doc.defaultView;
        var style = win.getComputedStyle ? win.getComputedStyle(el) : null;
        if ((style && (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse')) || el.hidden === true) {
          var hiddenErr = new Error('Element <' + el.tagName.toLowerCase() + (el.id ? ' id="' + el.id + '"' : '') + '> is not displayed and cannot be clicked.');
          hiddenErr.__wvd = 'element not interactable';
          throw hiddenErr;
        }
        // Options in a collapsed select have no rendered box; the WebDriver spec
        // treats them as interactable through their container. Handle before any
        // layout checks.
        if (el.tagName === 'OPTION') {
          var select = el.closest ? el.closest('select') : null;
          if (select) {
            if (select.multiple) { el.selected = !el.selected; } else { select.value = el.value; }
            select.dispatchEvent(new win.Event('input', { bubbles: true }));
            select.dispatchEvent(new win.Event('change', { bubbles: true }));
          }
          return null;
        }
        if (el.scrollIntoView) {
          try { el.scrollIntoView({ block: 'center', inline: 'center' }); }
          catch (e) { try { el.scrollIntoView(); } catch (e2) {} }
        }
        // Layout-dependent checks only where layout exists (jsdom has none).
        var hasLayout = doc.documentElement.getClientRects().length > 0;
        if (hasLayout && el.getClientRects().length === 0) {
          var sizeErr = new Error('Element <' + el.tagName.toLowerCase() + '> has no rendered box (zero size or detached from layout).');
          sizeErr.__wvd = 'element not interactable';
          throw sizeErr;
        }
        var rect = el.getBoundingClientRect();
        var cx = rect.left + rect.width / 2;
        var cy = rect.top + rect.height / 2;
        if (hasLayout && typeof doc.elementFromPoint === 'function') {
          var hit = doc.elementFromPoint(cx, cy);
          // elementFromPoint stops at shadow hosts; descend into open shadow
          // roots to find the deepest hit before deciding on interception.
          while (hit && hit.shadowRoot && typeof hit.shadowRoot.elementFromPoint === 'function') {
            var deeper = hit.shadowRoot.elementFromPoint(cx, cy);
            if (!deeper || deeper === hit) { break; }
            hit = deeper;
          }
          // Containment across shadow boundaries (composed tree walk).
          function composedContains(ancestor, node) {
            for (var n = node; n; n = n.parentNode || n.host || null) {
              if (n === ancestor) { return true; }
            }
            return false;
          }
          if (hit && hit !== el && !composedContains(el, hit) && !composedContains(hit, el)) {
            var hitDescription = '<' + hit.tagName.toLowerCase() + (hit.id ? ' id="' + hit.id + '"' : '') + '>';
            var interceptErr = new Error('Element ' + hitDescription + ' would receive the click at (' + Math.round(cx) + ', ' + Math.round(cy) + ').');
            interceptErr.__wvd = 'element click intercepted';
            throw interceptErr;
          }
        }
        var eventInit = {
          bubbles: true, cancelable: true, composed: true, view: win, button: 0, detail: 1,
          clientX: cx, clientY: cy
        };
        var PointerCtor = win.PointerEvent || win.MouseEvent;
        function fire(type, Ctor) {
          try { el.dispatchEvent(new Ctor(type, eventInit)); }
          catch (e) { el.dispatchEvent(new win.MouseEvent(type, eventInit)); }
        }
        fire('pointerdown', PointerCtor);
        fire('mousedown', win.MouseEvent);
        try { if (el.focus) { el.focus(); } } catch (e) {}
        fire('pointerup', PointerCtor);
        fire('mouseup', win.MouseEvent);
        if (typeof el.click === 'function') { el.click(); } else { fire('click', win.MouseEvent); }
        return null;
        """;

    public const string Clear = """
        var el = arguments[0];
        var win = el.ownerDocument.defaultView;
        var tag = el.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA') {
          if (el.value === '') { return null; } // spec: no-op (no events) when already empty
          var proto = tag === 'INPUT' ? win.HTMLInputElement.prototype : win.HTMLTextAreaElement.prototype;
          var descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
          if (descriptor && descriptor.set) { descriptor.set.call(el, ''); } else { el.value = ''; }
          el.dispatchEvent(new win.Event('input', { bubbles: true }));
          el.dispatchEvent(new win.Event('change', { bubbles: true }));
        } else if (el.isContentEditable) {
          if ((el.textContent || '') === '') { return null; }
          el.innerHTML = '';
        }
        return null;
        """;

    /// <summary>
    /// FR7: framework-safe typing. Uses the native value setter so React/Vue/Blazor
    /// controlled inputs observe the change, dispatches key + input events per
    /// character, and submits the owning form on Enter when keydown is not canceled.
    /// Supported control codepoints: Backspace, Tab, Enter, Escape, Space.
    /// </summary>
    public const string SendKeys = """
        var el = arguments[0], text = arguments[1];
        var win = el.ownerDocument.defaultView;
        if (el.disabled === true || el.readOnly === true) {
          var disabledErr = new Error('Element <' + el.tagName.toLowerCase() + (el.id ? ' id="' + el.id + '"' : '') + '> is ' + (el.disabled ? 'disabled' : 'read-only') + ' and cannot be typed into.');
          disabledErr.__wvd = 'element not interactable';
          throw disabledErr;
        }
        try { if (el.focus) { el.focus(); } } catch (e) {}
        var isField = el.tagName === 'INPUT' || el.tagName === 'TEXTAREA';
        var descriptor = null;
        if (isField) {
          var proto = el.tagName === 'INPUT' ? win.HTMLInputElement.prototype : win.HTMLTextAreaElement.prototype;
          descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
        }
        function setValue(next) {
          if (descriptor && descriptor.set) { descriptor.set.call(el, next); } else { el.value = next; }
        }
        function fireKey(type, key) {
          try { return el.dispatchEvent(new win.KeyboardEvent(type, { bubbles: true, cancelable: true, key: key })); }
          catch (e) { return true; }
        }
        function fireInput(data, inputType) {
          var event;
          try { event = new win.InputEvent('input', { bubbles: true, data: data, inputType: inputType }); }
          catch (e) { event = new win.Event('input', { bubbles: true }); }
          el.dispatchEvent(event);
        }
        var special = { '\uE003': 'Backspace', '\uE004': 'Tab', '\uE006': 'Enter', '\uE007': 'Enter', '\uE00C': 'Escape', '\uE00D': ' ' };
        var mutated = false;
        for (var i = 0; i < text.length; i++) {
          var ch = text[i];
          var key = special[ch];
          if (key === 'Backspace') {
            fireKey('keydown', key);
            if (isField && el.value.length) { setValue(el.value.slice(0, -1)); fireInput(null, 'deleteContentBackward'); mutated = true; }
            fireKey('keyup', key);
            continue;
          }
          if (key === 'Enter') {
            var proceed = fireKey('keydown', key);
            fireKey('keyup', key);
            if (proceed && isField && el.tagName === 'INPUT' && el.form) {
              // Browsers commit the value (change) before submitting on Enter.
              if (mutated) { el.dispatchEvent(new win.Event('change', { bubbles: true })); mutated = false; }
              if (typeof el.form.requestSubmit === 'function') { el.form.requestSubmit(); }
              else { el.form.dispatchEvent(new win.Event('submit', { bubbles: true, cancelable: true })); }
            }
            continue;
          }
          if (key && key !== ' ') {
            fireKey('keydown', key);
            fireKey('keyup', key);
            continue;
          }
          var printable = key === ' ' ? ' ' : ch;
          if (printable.charCodeAt(0) >= 0xE000 && printable.charCodeAt(0) <= 0xF8FF) { continue; }
          fireKey('keydown', printable);
          if (isField) {
            setValue(el.value + printable);
            fireInput(printable, 'insertText');
            mutated = true;
          } else if (el.isContentEditable) {
            el.textContent = (el.textContent || '') + printable;
            fireInput(printable, 'insertText');
            mutated = true;
          }
          fireKey('keyup', printable);
        }
        if (mutated && isField) { el.dispatchEvent(new win.Event('change', { bubbles: true })); }
        return null;
        """;

    public const string GetText = """
        var el = arguments[0];
        var text = typeof el.innerText === 'string' ? el.innerText : (el.textContent || '');
        return text.replace(/^\s+|\s+$/g, '');
        """;

    public const string GetDomAttribute = "var el = arguments[0]; return el.getAttribute(arguments[1]);";

    public const string GetProperty = "var el = arguments[0]; var v = el[arguments[1]]; return v === undefined ? null : v;";

    public const string GetCssValue = """
        var el = arguments[0];
        var win = el.ownerDocument.defaultView;
        return win.getComputedStyle(el).getPropertyValue(arguments[1]) || '';
        """;

    public const string GetRect = """
        var el = arguments[0];
        var win = el.ownerDocument.defaultView;
        var rect = el.getBoundingClientRect();
        return {
          x: rect.left + (win.scrollX || win.pageXOffset || 0),
          y: rect.top + (win.scrollY || win.pageYOffset || 0),
          width: rect.width,
          height: rect.height
        };
        """;

    /// <summary>Viewport-relative rect for WKSnapshotConfiguration.Rect (element screenshots).</summary>
    public const string GetViewportRect = """
        var el = arguments[0];
        if (el.scrollIntoView) {
          try { el.scrollIntoView({ block: 'center', inline: 'center' }); }
          catch (e) { try { el.scrollIntoView(); } catch (e2) {} }
        }
        var rect = el.getBoundingClientRect();
        return { x: rect.left, y: rect.top, width: rect.width, height: rect.height };
        """;

    public const string GetTagName = "return arguments[0].tagName.toLowerCase();";

    public const string IsEnabled = """
        var el = arguments[0];
        if (el.disabled === true) { return false; }
        if (el.closest && el.closest('fieldset[disabled]')) { return false; }
        return true;
        """;

    public const string IsSelected = """
        var el = arguments[0];
        if (el.tagName === 'OPTION') { return !!el.selected; }
        return !!el.checked;
        """;

    public const string IsDisplayed = """
        var el = arguments[0];
        if (!el.isConnected) { return false; }
        var win = el.ownerDocument.defaultView;
        var style = win.getComputedStyle ? win.getComputedStyle(el) : null;
        if (style && (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse')) { return false; }
        if (el.hidden === true) { return false; }
        return el.getClientRects().length > 0;
        """;

    public const string GetShadowRoot = """
        var el = arguments[0];
        var root = el.shadowRoot;
        if (!root) {
          var err = new Error('Element <' + el.tagName.toLowerCase() + '> has no open shadow root.');
          err.__wvd = 'no such shadow root';
          throw err;
        }
        return root;
        """;

    /// <summary>
    /// args: null | frame index (number) | iframe element. Runs in the current
    /// context's realm, so indexes resolve relative to the selected frame (W3C).
    /// </summary>
    public const string SwitchFrame = """
        var target = arguments[0];
        var bridge = window.top.__wvd1;
        var next = null;
        if (target === null || target === undefined) {
          bridge.ctx = null;
          return null;
        }
        if (typeof target === 'number') {
          if (target < 0 || target >= window.frames.length) {
            var idxErr = new Error('No frame at index ' + target + ' (frame count: ' + window.frames.length + ').');
            idxErr.__wvd = 'no such frame';
            throw idxErr;
          }
          next = window.frames[target];
        } else {
          var tag = target.tagName ? target.tagName.toUpperCase() : '';
          if (tag !== 'IFRAME' && tag !== 'FRAME') {
            var tagErr = new Error('Element <' + tag.toLowerCase() + '> is not a frame.');
            tagErr.__wvd = 'no such frame';
            throw tagErr;
          }
          next = target.contentWindow;
        }
        try {
          if (!next) { throw 0; }
          void next.document; // throws for cross-origin frames
        } catch (e) {
          var originErr = new Error('The frame is not reachable (detached or cross-origin; only same-origin frames are supported).');
          originErr.__wvd = 'no such frame';
          throw originErr;
        }
        bridge.ctx = (next === window.top) ? null : next;
        return null;
        """;

    public const string SwitchParentFrame = """
        var bridge = window.top.__wvd1;
        var parent = window.parent || window;
        bridge.ctx = (parent === window.top) ? null : parent;
        return null;
        """;

    public const string GetActiveElement = "return document.activeElement || document.body;";

    public const string GetTitle = "return window.top.document.title;";
    public const string GetUrl = "return window.top.location.href;";
    public const string GetPageSource = "return (document.documentElement && document.documentElement.outerHTML) || '';";

    public const string NavigateTo = "window.top.location.assign(arguments[0]); return null;";
    public const string Refresh = "window.top.location.reload(); return null;";
    public const string GoBack = "window.top.history.back(); return null;";
    public const string GoForward = "window.top.history.forward(); return null;";

    /// <summary>
    /// Navigation waits compare docId: a fresh document (or a bfcache restore of a
    /// different one) gets a different bridge docId; same-document navigations
    /// (hash changes) keep it and are detected by URL change instead.
    /// </summary>
    public const string ReadNavigationState = """
        var top = window.top;
        return {
          docId: top.__wvd1 ? top.__wvd1.docId : null,
          ready: top.document.readyState,
          url: top.location.href
        };
        """;

    public const string GetWindowRect = """
        return { x: window.screenX || 0, y: window.screenY || 0, width: window.innerWidth, height: window.innerHeight };
        """;

    /// <summary>Translates a W3C locator strategy into the bridge's css/xpath pair.</summary>
    public static (string Strategy, string Selector) TranslateLocator(string mechanism, string value)
    {
        switch (mechanism)
        {
            case "css selector":
                return ("css", value);
            case "xpath":
                return ("xpath", value);
            case "tag name":
                return ("css", value);
            case "link text":
                return ("xpath", $".//a[normalize-space(.)={XPathLiteral(value)}]");
            case "partial link text":
                return ("xpath", $".//a[contains(normalize-space(.), {XPathLiteral(value)})]");
            case "id":
                return ("css", $"[id={CssAttributeLiteral(value)}]");
            case "name":
                return ("css", $"[name={CssAttributeLiteral(value)}]");
            case "class name":
                return ("xpath", $".//*[contains(concat(' ', normalize-space(@class), ' '), {XPathLiteral($" {value} ")})]");
            default:
                throw new InvalidSelectorException($"Locator strategy '{mechanism}' is not supported by WebViewDriver.");
        }
    }

    private static string CssAttributeLiteral(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Builds an XPath string literal, handling values containing both quote kinds.</summary>
    internal static string XPathLiteral(string value)
    {
        if (!value.Contains('\''))
        {
            return $"'{value}'";
        }

        if (!value.Contains('"'))
        {
            return $"\"{value}\"";
        }

        var parts = value.Split('\'');
        var pieces = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                pieces.Add("\"'\"");
            }

            if (parts[i].Length > 0)
            {
                pieces.Add($"'{parts[i]}'");
            }
        }

        return $"concat({string.Join(", ", pieces)})";
    }
}
