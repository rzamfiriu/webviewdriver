namespace WebViewDriver.Selenium.Actions;

/// <summary>
/// The in-page W3C Actions interpreter used by <see cref="DomActionsBackend"/>.
/// Steps through ticks (the i-th action of every input source runs in tick i),
/// synthesizing pointer/mouse/keyboard/wheel events with correct hover
/// transitions, click/dblclick synthesis and modifier state. Input state lives
/// on the bridge (window.top.__wvd1.inputState) so it persists across
/// Perform() calls, per spec; navigation resets it.
/// </summary>
internal static class ActionsScripts
{
    /// <summary>args: [sequences] — W3C action sequences; element origins arrive dereferenced.</summary>
    public const string Perform = """
        var sequences = arguments[0];
        var bridge = window.top.__wvd1;
        var state = bridge.inputState || (bridge.inputState = {
          x: 0, y: 0,
          buttons: {},                 // button -> { target }
          keys: {},                    // key name -> true
          modifiers: { Shift: false, Control: false, Alt: false, Meta: false },
          hoverTarget: null,
          originEl: null,              // layout-less fallback event target
          lastClick: { target: null, time: 0, count: 0 }
        });

        function winOf(el) { return (el && el.ownerDocument && el.ownerDocument.defaultView) || window; }
        function hasLayout() { return document.documentElement.getClientRects().length > 0; }
        function buttonsMask() {
          var mask = 0;
          for (var b in state.buttons) { mask |= [1, 4, 2, 8, 16][b] || 0; }
          return mask;
        }
        function withModifiers(extra, win) {
          var init = {
            bubbles: true, cancelable: true, composed: true, view: win,
            shiftKey: state.modifiers.Shift, ctrlKey: state.modifiers.Control,
            altKey: state.modifiers.Alt, metaKey: state.modifiers.Meta
          };
          for (var k in extra) { init[k] = extra[k]; }
          return init;
        }
        function hitTest(x, y) {
          if (hasLayout() && typeof document.elementFromPoint === 'function') {
            var hit = document.elementFromPoint(x, y);
            while (hit && hit.shadowRoot && typeof hit.shadowRoot.elementFromPoint === 'function') {
              var deeper = hit.shadowRoot.elementFromPoint(x, y);
              if (!deeper || deeper === hit) { break; }
              hit = deeper;
            }
            if (hit) { return hit; }
          }
          return state.originEl || document.body || document.documentElement;
        }
        function firePointer(type, target, x, y, button) {
          var win = winOf(target);
          var init = withModifiers({
            clientX: x, clientY: y, button: button || 0, buttons: buttonsMask(), detail: 1,
            relatedTarget: null
          }, win);
          var PointerCtor = win.PointerEvent || win.MouseEvent;
          try { target.dispatchEvent(new PointerCtor(type, init)); }
          catch (e) { target.dispatchEvent(new win.MouseEvent(type, init)); }
        }
        function fireMouse(type, target, x, y, button, detail, related) {
          var win = winOf(target);
          var init = withModifiers({
            clientX: x, clientY: y, button: button || 0, buttons: buttonsMask(),
            detail: detail === undefined ? 1 : detail, relatedTarget: related || null
          }, win);
          return target.dispatchEvent(new win.MouseEvent(type, init));
        }
        function updateHover(newTarget, x, y) {
          var old = state.hoverTarget;
          if (old === newTarget) { return; }
          if (old && old.isConnected) {
            firePointer('pointerout', old, x, y, 0);
            fireMouse('mouseout', old, x, y, 0, 0, newTarget);
            fireMouse('mouseleave', old, x, y, 0, 0, newTarget);
          }
          if (newTarget) {
            firePointer('pointerover', newTarget, x, y, 0);
            fireMouse('mouseover', newTarget, x, y, 0, 0, old);
            fireMouse('mouseenter', newTarget, x, y, 0, 0, old);
          }
          state.hoverTarget = newTarget;
        }

        function doPointerMove(action) {
          var nx, ny;
          var origin = action.origin;
          if (origin && typeof origin === 'object' && origin.nodeType === 1) {
            var rect = origin.getBoundingClientRect();
            nx = rect.left + rect.width / 2 + (action.x || 0);
            ny = rect.top + rect.height / 2 + (action.y || 0);
            state.originEl = origin;
          } else if (origin === 'pointer') {
            nx = state.x + (action.x || 0);
            ny = state.y + (action.y || 0);
          } else { // 'viewport' or unspecified
            nx = action.x || 0;
            ny = action.y || 0;
          }
          var target = hitTest(nx, ny);
          updateHover(target, nx, ny);
          firePointer('pointermove', target, nx, ny, 0);
          fireMouse('mousemove', target, nx, ny, 0);
          state.x = nx;
          state.y = ny;
        }
        function doPointerDown(action) {
          var button = action.button || 0;
          var target = hitTest(state.x, state.y);
          state.buttons[button] = { target: target };
          firePointer('pointerdown', target, state.x, state.y, button);
          fireMouse('mousedown', target, state.x, state.y, button);
          try { if (target.focus) { target.focus(); } } catch (e) {}
        }
        function doPointerUp(action) {
          var button = action.button || 0;
          var pressed = state.buttons[button];
          delete state.buttons[button];
          var target = hitTest(state.x, state.y);
          firePointer('pointerup', target, state.x, state.y, button);
          fireMouse('mouseup', target, state.x, state.y, button);
          if (pressed && pressed.target === target && button === 0) {
            var now = Date.now();
            if (state.lastClick.target === target && (now - state.lastClick.time) < 500) {
              state.lastClick.count++;
            } else {
              state.lastClick.count = 1;
            }
            state.lastClick.target = target;
            state.lastClick.time = now;
            fireMouse('click', target, state.x, state.y, 0, state.lastClick.count);
            if (typeof target.click === 'function' && state.lastClick.count === 1) {
              // native activation behavior (checkboxes, labels) once per click pair
            }
            if (state.lastClick.count === 2) {
              fireMouse('dblclick', target, state.x, state.y, 0, 2);
              state.lastClick.count = 0;
            }
          }
        }

        var MODIFIERS = { Shift: 'Shift', Control: 'Control', Alt: 'Alt', Meta: 'Meta' };
        function keyTarget() { return document.activeElement || document.body; }
        function fireKey(type, key, target) {
          var win = winOf(target);
          var init = withModifiers({ key: key, cancelable: true }, win);
          try { return target.dispatchEvent(new win.KeyboardEvent(type, init)); }
          catch (e) { return true; }
        }
        function typeIntoTarget(target, ch) {
          var win = winOf(target);
          var isField = target.tagName === 'INPUT' || target.tagName === 'TEXTAREA';
          if (isField && !(target.disabled || target.readOnly)) {
            var proto = target.tagName === 'INPUT' ? win.HTMLInputElement.prototype : win.HTMLTextAreaElement.prototype;
            var descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
            if (descriptor && descriptor.set) { descriptor.set.call(target, target.value + ch); } else { target.value += ch; }
            var event;
            try { event = new win.InputEvent('input', { bubbles: true, data: ch, inputType: 'insertText' }); }
            catch (e) { event = new win.Event('input', { bubbles: true }); }
            target.dispatchEvent(event);
          } else if (target.isContentEditable) {
            target.textContent = (target.textContent || '') + ch;
            target.dispatchEvent(new win.Event('input', { bubbles: true }));
          }
        }
        function doKeyDown(action) {
          var key = action.wvdKey;
          state.keys[key] = true;
          if (MODIFIERS[key]) { state.modifiers[key] = true; }
          var target = keyTarget();
          var proceed = fireKey('keydown', key, target);
          if (proceed && key.length === 1 && !state.modifiers.Control && !state.modifiers.Meta && !state.modifiers.Alt) {
            typeIntoTarget(target, key);
          }
        }
        function doKeyUp(action) {
          var key = action.wvdKey;
          delete state.keys[key];
          if (MODIFIERS[key]) { state.modifiers[key] = false; }
          fireKey('keyup', key, keyTarget());
        }

        function doWheel(action) {
          var target;
          var origin = action.origin;
          if (origin && typeof origin === 'object' && origin.nodeType === 1) {
            target = origin;
          } else {
            target = hitTest(action.x || state.x, action.y || state.y);
          }
          var win = winOf(target);
          var init = withModifiers({ deltaX: action.deltaX || 0, deltaY: action.deltaY || 0, deltaMode: 0 }, win);
          var proceed;
          try { proceed = target.dispatchEvent(new win.WheelEvent('wheel', init)); }
          catch (e) { proceed = true; }
          if (proceed) {
            try {
              if (target === win.document.scrollingElement || target === win.document.body || target === win.document.documentElement) {
                win.scrollBy(action.deltaX || 0, action.deltaY || 0);
              } else if (typeof target.scrollBy === 'function') {
                target.scrollBy(action.deltaX || 0, action.deltaY || 0);
              } else {
                target.scrollTop += (action.deltaY || 0);
                target.scrollLeft += (action.deltaX || 0);
              }
            } catch (e) {}
          }
        }

        return (async function () {
          var tickCount = 0;
          for (var s = 0; s < sequences.length; s++) {
            tickCount = Math.max(tickCount, (sequences[s].actions || []).length);
          }
          for (var t = 0; t < tickCount; t++) {
            var pause = 0;
            for (var i = 0; i < sequences.length; i++) {
              var source = sequences[i];
              var action = (source.actions || [])[t];
              if (!action) { continue; }
              if (action.type === 'pause') {
                pause = Math.max(pause, action.duration || 0);
                continue;
              }
              if (source.type === 'pointer') {
                if (action.type === 'pointerMove') { doPointerMove(action); pause = Math.max(pause, Math.min(action.duration || 0, 250)); }
                else if (action.type === 'pointerDown') { doPointerDown(action); }
                else if (action.type === 'pointerUp') { doPointerUp(action); }
                else if (action.type === 'pointerCancel') { state.buttons = {}; }
              } else if (source.type === 'key') {
                if (action.type === 'keyDown') { doKeyDown(action); }
                else if (action.type === 'keyUp') { doKeyUp(action); }
              } else if (source.type === 'wheel') {
                if (action.type === 'scroll') { doWheel(action); }
              }
            }
            if (pause > 0) {
              await new Promise(function (resolve) { setTimeout(resolve, Math.min(pause, 2000)); });
            }
          }
          return null;
        })();
        """;

    /// <summary>W3C Release Actions: undo everything still pressed, clear state.</summary>
    public const string Release = """
        var bridge = window.top.__wvd1;
        var state = bridge.inputState;
        if (!state) { return null; }
        function winOf(el) { return (el && el.ownerDocument && el.ownerDocument.defaultView) || window; }
        for (var b in state.buttons) {
          var pressed = state.buttons[b];
          var target = (pressed.target && pressed.target.isConnected) ? pressed.target : document.body;
          var win = winOf(target);
          var init = { bubbles: true, cancelable: true, composed: true, view: win, clientX: state.x, clientY: state.y, button: Number(b) };
          var PointerCtor = win.PointerEvent || win.MouseEvent;
          try { target.dispatchEvent(new PointerCtor('pointerup', init)); } catch (e) {}
          try { target.dispatchEvent(new win.MouseEvent('mouseup', init)); } catch (e) {}
        }
        for (var key in state.keys) {
          var kt = document.activeElement || document.body;
          try { kt.dispatchEvent(new winOf(kt).KeyboardEvent('keyup', { bubbles: true, key: key })); } catch (e) {}
        }
        bridge.inputState = null;
        return null;
        """;
}
