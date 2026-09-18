// WebViewDriver in-page bridge (protocol v1).
// Injected lazily by the test-side client; the app host never sees this file.
// Keeps a stable element/shadow-root registry, tracks the current browsing
// context (same-origin frames), and exposes a single generic entry point
// (exec) that runs command scripts shipped from the client in the current
// context's realm. All WebDriver semantics stay client-side.
(function () {
  'use strict';
  if (window.__wvd1) { return; }

  var ELEMENT_KEY = 'element-6066-11e4-a52e-4f735466cecf';
  var SHADOW_KEY = 'shadow-6066-11e4-a52e-4f735466cecf';

  var nodes = {};               // id -> Element | ShadowRoot
  var nodeIds = typeof WeakMap === 'function' ? new WeakMap() : null;
  var nextNodeId = 1;
  var pending = {};             // id -> { done: bool, envelope: string }
  var nextPendingId = 1;
  var execCount = 0;

  function wvdError(code, message) {
    var e = new Error(message || code);
    e.__wvd = code;
    return e;
  }

  // Cross-realm-safe type checks: elements can come from same-origin frame
  // documents, where instanceof against this realm's constructors fails.
  function tag(v) { return Object.prototype.toString.call(v); }
  function isElement(v) {
    return !!v && typeof v === 'object' && v.nodeType === 1 && typeof v.nodeName === 'string';
  }
  function isShadowRoot(v) {
    return !!v && typeof v === 'object' && v.nodeType === 11 && isElement(v.host);
  }
  function isNodeCollection(v) {
    var t = tag(v);
    return t === '[object NodeList]' || t === '[object HTMLCollection]';
  }

  function elementIsLive(el) {
    if (el.isConnected === false) { return false; }
    // An element can be "connected" to a document whose iframe was removed.
    var doc = el.ownerDocument;
    return !!doc && !!doc.defaultView;
  }

  // FR5: registry must not leak. Strong map swept periodically; dead entries
  // are dropped and later dereferences surface as stale references.
  function sweep() {
    for (var id in nodes) {
      var node = nodes[id];
      if (!node) { delete nodes[id]; continue; }
      if (isShadowRoot(node)) {
        if (!elementIsLive(node.host)) { delete nodes[id]; }
      } else if (!elementIsLive(node)) {
        delete nodes[id];
      }
    }
  }

  function ref(node) {
    if (nodeIds) {
      var existing = nodeIds.get(node);
      if (existing && nodes[existing] === node) { return existing; }
    }
    var id = 'wvd-' + (nextNodeId++);
    nodes[id] = node;
    if (nodeIds) { nodeIds.set(node, id); }
    return id;
  }

  function derefElement(id) {
    var el = nodes[id];
    if (!el || isShadowRoot(el)) {
      throw wvdError('stale element reference', 'Element reference ' + id + ' is unknown; the page may have navigated.');
    }
    if (!elementIsLive(el)) {
      delete nodes[id];
      throw wvdError('stale element reference', 'The element is no longer attached to the document.');
    }
    return el;
  }

  function derefShadowRoot(id) {
    var root = nodes[id];
    if (!root || !isShadowRoot(root)) {
      throw wvdError('detached shadow root', 'Shadow root reference ' + id + ' is unknown; the page may have navigated.');
    }
    if (!elementIsLive(root.host)) {
      delete nodes[id];
      throw wvdError('detached shadow root', 'The shadow root host is no longer attached to the document.');
    }
    return root;
  }

  function serialize(value, seen, depth) {
    if (value === null || value === undefined) { return null; }
    var t = typeof value;
    if (t === 'boolean' || t === 'string') { return value; }
    if (t === 'number') { return isFinite(value) ? value : null; }
    if (t === 'function' || t === 'symbol') { return null; }
    if (isElement(value)) {
      var elRef = {};
      elRef[ELEMENT_KEY] = ref(value);
      return elRef;
    }
    if (isShadowRoot(value)) {
      var srRef = {};
      srRef[SHADOW_KEY] = ref(value);
      return srRef;
    }
    if (seen.indexOf(value) !== -1) {
      throw wvdError('javascript error', 'Cannot serialize a value with a circular reference.');
    }
    if (depth > 100) {
      throw wvdError('javascript error', 'Cannot serialize: object graph too deep.');
    }
    seen = seen.concat([value]);
    if (Array.isArray(value) || isNodeCollection(value)) {
      var arr = [];
      for (var i = 0; i < value.length; i++) { arr.push(serialize(value[i], seen, depth + 1)); }
      return arr;
    }
    if (tag(value) === '[object Date]') { return value.toISOString(); }
    if (tag(value) === '[object Window]') {
      throw wvdError('javascript error', 'Cannot serialize a Window object.');
    }
    var out = {};
    for (var k in value) {
      if (Object.prototype.hasOwnProperty.call(value, k)) {
        out[k] = serialize(value[k], seen, depth + 1);
      }
    }
    return out;
  }

  function deserialize(value) {
    if (value === null || typeof value !== 'object') { return value; }
    if (Array.isArray(value)) {
      var arr = [];
      for (var i = 0; i < value.length; i++) { arr.push(deserialize(value[i])); }
      return arr;
    }
    if (typeof value[ELEMENT_KEY] === 'string') { return derefElement(value[ELEMENT_KEY]); }
    if (typeof value[SHADOW_KEY] === 'string') { return derefShadowRoot(value[SHADOW_KEY]); }
    var out = {};
    for (var k in value) {
      if (Object.prototype.hasOwnProperty.call(value, k)) { out[k] = deserialize(value[k]); }
    }
    return out;
  }

  // The current browsing context. Always evaluated fresh so a removed iframe
  // surfaces as "no such window" instead of acting on a dead realm.
  function currentContext() {
    var ctx = bridge.ctx || window;
    if (ctx === window) { return ctx; }
    try {
      if (ctx.closed) { throw 0; }
      void ctx.document; // throws when the realm is gone or cross-origin
      if (ctx !== ctx.top && (!ctx.frameElement || !elementIsLive(ctx.frameElement))) { throw 0; }
      return ctx;
    } catch (e) {
      throw wvdError('no such window', 'The selected frame has been removed or is no longer reachable; switch back to the top-level context.');
    }
  }

  function okEnvelope(value) {
    return JSON.stringify({ s: 'ok', v: serialize(value, [], 0) });
  }

  function errorEnvelope(e) {
    return JSON.stringify({
      s: 'error',
      error: (e && e.__wvd) || 'javascript error',
      message: String((e && e.message) || e),
      stack: e && e.stack ? String(e.stack) : null
    });
  }

  function trackPromise(promise) {
    var id = nextPendingId++;
    var entry = { done: false, envelope: null };
    pending[id] = entry;
    promise.then(
      function (v) {
        try { entry.envelope = okEnvelope(v); } catch (e) { entry.envelope = errorEnvelope(e); }
        entry.done = true;
      },
      function (e) {
        entry.envelope = errorEnvelope(e);
        entry.done = true;
      }
    );
    return JSON.stringify({ s: 'pending', id: id });
  }

  // Single entry point. fnBody is a JavaScript function body (W3C executeScript
  // style, receives `arguments`); argsJson is a JSON array whose element and
  // shadow-root references are resolved before the call. The function is
  // created in the current browsing context's realm, so unqualified `document`
  // and `window` resolve to the selected frame. options.async appends a
  // resolve callback as the final argument (executeAsyncScript semantics).
  function exec(fnBody, argsJson, options) {
    try {
      if ((++execCount % 25) === 0) { sweep(); }
      var ctx = currentContext();
      var FunctionCtor = ctx === window ? Function : (ctx.Function || Function);
      var args = deserialize(JSON.parse(argsJson || '[]'));
      if (options && options.async) {
        var resolveFn = null;
        var promise = new Promise(function (resolve) { resolveFn = resolve; });
        args.push(resolveFn);
        var token = trackPromise(promise);
        new FunctionCtor(fnBody).apply(null, args);
        return token;
      }
      var result = new FunctionCtor(fnBody).apply(null, args);
      if (result && typeof result.then === 'function') {
        return trackPromise(result);
      }
      return okEnvelope(result);
    } catch (e) {
      return errorEnvelope(e);
    }
  }

  function poll(pendingId) {
    var entry = pending[pendingId];
    if (!entry) {
      return JSON.stringify({ s: 'error', error: 'javascript error', message: 'Unknown pending script id ' + pendingId + '.' });
    }
    if (!entry.done) {
      return JSON.stringify({ s: 'pending', id: pendingId });
    }
    delete pending[pendingId];
    return entry.envelope;
  }

  var bridge = {
    version: 1,
    // Identifies this document instance: navigation waits detect a fresh
    // document (or a bfcache restore of an older one) by docId change.
    docId: 'doc-' + Math.random().toString(36).slice(2) + '-' + Date.now().toString(36),
    ctx: null, // null = top-level browsing context
    exec: exec,
    poll: poll
  };
  window.__wvd1 = bridge;
})();
