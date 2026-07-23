"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const {
  imageryCoverageHeader,
  gibsTileApiUrl,
  loadGibsTile,
  createGibsWarningReporter,
  createGoogleMapsLoader
} = require("../src/ThermalWatch.Viewer/wwwroot/map-support.js");

class FakeImage {
  constructor() {
    this.onload = null;
    this.onerror = null;
    this.requests = [];
    this.sourceRemoved = false;
  }

  set src(value) {
    this.requests.push(value);
  }

  succeed() {
    this.onload?.();
  }

  fail() {
    this.onerror?.();
  }

  removeAttribute(name) {
    if (name === "src")
      this.sourceRemoved = true;
  }
}

class FakeAbortController {
  constructor() {
    this.signal = { aborted: false };
  }

  abort() {
    this.signal.aborted = true;
  }
}

function imageryResponse({ status = 200, mediaType = "image/png", coverage = "complete" } = {}) {
  const headers = new Map([
    ["content-type", mediaType],
    [imageryCoverageHeader.toLowerCase(), coverage]
  ]);
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: {
      get(name) {
        return headers.get(name.toLowerCase()) ?? null;
      }
    },
    async blob() {
      return { type: mediaType };
    }
  };
}

function flushAsyncWork() {
  return new Promise(resolve => setImmediate(resolve));
}

function googleEnvironment() {
  const scripts = [];
  const timeouts = new Map();
  let nextTimeoutId = 1;

  const documentObject = {
    createElement(tagName) {
      assert.equal(tagName, "script");
      const listeners = new Map();
      return {
        removed: false,
        addEventListener(eventName, handler) {
          listeners.set(eventName, handler);
        },
        dispatch(eventName) {
          listeners.get(eventName)?.();
        },
        remove() {
          this.removed = true;
        }
      };
    },
    head: {
      append(script) {
        scripts.push(script);
      }
    }
  };

  return {
    windowObject: {},
    documentObject,
    scripts,
    timeouts,
    setTimeoutFunction(handler) {
      const id = nextTimeoutId;
      nextTimeoutId += 1;
      timeouts.set(id, handler);
      return id;
    },
    clearTimeoutFunction(id) {
      timeouts.delete(id);
    },
    runTimeout() {
      const [id, handler] = timeouts.entries().next().value;
      timeouts.delete(id);
      handler();
    }
  };
}

function createLoader(environment) {
  return createGoogleMapsLoader({
    windowObject: environment.windowObject,
    documentObject: environment.documentObject,
    setTimeoutFunction: environment.setTimeoutFunction,
    clearTimeoutFunction: environment.clearTimeoutFunction,
    timeoutMilliseconds: 5
  });
}

test("GIBS tile URLs use only the same-origin viewer API", () => {
  assert.equal(
    gibsTileApiUrl({ z: 6, x: 37, y: 21 }),
    "/api/viewer/imagery/gibs/6/37/21.png");
  assert.throws(() => gibsTileApiUrl({ z: 6, x: 37.5, y: 21 }), /Integer map tile/);
});

test("GIBS tiles load API PNGs and expose complete coverage", async () => {
  const image = new FakeImage();
  const requests = [];
  const revoked = [];
  let completed = null;
  const controller = new FakeAbortController();
  loadGibsTile(image, { z: 3, x: 1, y: 2 }, {
    fetchFunction: async (url, options) => {
      requests.push({ url, options });
      return imageryResponse();
    },
    createObjectUrl: () => "blob:complete-tile",
    revokeObjectUrl: url => revoked.push(url),
    createAbortController: () => controller,
    onComplete: result => { completed = result; }
  });

  await flushAsyncWork();
  assert.equal(requests.length, 1);
  assert.equal(requests[0].url, "/api/viewer/imagery/gibs/3/1/2.png");
  assert.equal(requests[0].options.headers.Accept, "image/png");
  assert.equal(requests[0].options.signal, controller.signal);
  assert.deepEqual(image.requests, ["blob:complete-tile"]);

  image.succeed();
  assert.deepEqual(completed, { coverage: "complete" });
  assert.deepEqual(revoked, ["blob:complete-tile"]);
});

test("GIBS tiles pass partial coverage to the warning boundary", async () => {
  const image = new FakeImage();
  let completed = null;
  loadGibsTile(image, { z: 2, x: 1, y: 1 }, {
    fetchFunction: async () => imageryResponse({ coverage: "partial" }),
    createObjectUrl: () => "blob:partial-tile",
    revokeObjectUrl: () => {},
    onComplete: result => { completed = result; }
  });

  await flushAsyncWork();
  image.succeed();
  assert.deepEqual(completed, { coverage: "partial" });
});

test("GIBS tile cancellation aborts fetch and ignores late completion", async () => {
  const image = new FakeImage();
  const controller = new FakeAbortController();
  let resolveRequest;
  let completed = false;
  const request = new Promise(resolve => { resolveRequest = resolve; });
  const cancel = loadGibsTile(image, { z: 2, x: 1, y: 1 }, {
    fetchFunction: () => request,
    createAbortController: () => controller,
    onComplete: () => { completed = true; }
  });

  cancel();
  resolveRequest(imageryResponse());
  await flushAsyncWork();
  assert.equal(controller.signal.aborted, true);
  assert.equal(image.sourceRemoved, true);
  assert.equal(image.requests.length, 0);
  assert.equal(completed, false);
});

test("GIBS tile cancellation revokes a prepared object URL", async () => {
  const image = new FakeImage();
  const revoked = [];
  const cancel = loadGibsTile(image, { z: 2, x: 1, y: 1 }, {
    fetchFunction: async () => imageryResponse(),
    createObjectUrl: () => "blob:pending-tile",
    revokeObjectUrl: url => revoked.push(url)
  });

  await flushAsyncWork();
  cancel();
  assert.equal(image.sourceRemoved, true);
  assert.deepEqual(revoked, ["blob:pending-tile"]);
});

test("GIBS tile failures do not produce image content", async () => {
  const image = new FakeImage();
  let error = null;
  loadGibsTile(image, { z: 2, x: 1, y: 1 }, {
    fetchFunction: async () => imageryResponse({ status: 502 }),
    onError: value => { error = value; }
  });

  await flushAsyncWork();
  assert.match(error.message, /HTTP 502/);
  assert.equal(image.requests.length, 0);
});

test("GIBS warning reporter ignores complete tiles and deduplicates degraded coverage", () => {
  const messages = [];
  const report = createGibsWarningReporter(message => messages.push(message));

  report({ coverage: "complete" });
  report({ coverage: "partial" });
  report({ coverage: "none" });

  assert.equal(messages.length, 1);
  assert.match(messages[0], /unresolved pixels use the neutral map background/);
});

test("browser runtime contains no direct FIRMS or GIBS request host", () => {
  const viewerRoot = join(__dirname, "../src/ThermalWatch.Viewer/wwwroot");
  const runtime = ["index.html", "app.js", "map-support.js"]
    .map(file => readFileSync(join(viewerRoot, file), "utf8"))
    .join("\n");

  assert.doesNotMatch(runtime, /gibs\.earthdata\.nasa\.gov|firms\.modaps/i);
});

test("Google loader resolves immediately when the map API is already present", async () => {
  const environment = googleEnvironment();
  environment.windowObject.google = { maps: {} };
  const loader = createLoader(environment);

  await loader.load("browser-key");
  assert.equal(environment.scripts.length, 0);
  assert.equal(environment.timeouts.size, 0);
});

test("Google loader resolves callback success and builds an asynchronous script request", async () => {
  const environment = googleEnvironment();
  const loader = createLoader(environment);
  const loading = loader.load("browser key");

  assert.equal(environment.scripts.length, 1);
  assert.match(environment.scripts[0].src, /key=browser%20key/);
  assert.match(environment.scripts[0].src, /loading=async/);
  environment.windowObject.google = { maps: {} };
  environment.windowObject.__thermalWatchGoogleMapsReady();

  await loading;
  assert.equal(environment.timeouts.size, 0);
  assert.equal(environment.windowObject.__thermalWatchGoogleMapsReady, undefined);
  assert.equal(environment.scripts[0].removed, false);
});

test("Google loader rejects a callback that does not expose the map API", async () => {
  const environment = googleEnvironment();
  const loader = createLoader(environment);
  const loading = loader.load("browser-key");

  environment.windowObject.__thermalWatchGoogleMapsReady();
  await assert.rejects(loading, /without exposing its map API/);
  assert.equal(environment.scripts[0].removed, true);
  assert.equal(environment.windowObject.__thermalWatchGoogleMapsReady, undefined);
  assert.equal(environment.timeouts.size, 0);
});

test("Google loader rejects a script error, cleans up, and permits retry", async () => {
  const environment = googleEnvironment();
  const loader = createLoader(environment);
  const firstAttempt = loader.load("browser-key");
  environment.scripts[0].dispatch("error");

  await assert.rejects(firstAttempt, /could not be downloaded/);
  assert.equal(environment.scripts[0].removed, true);
  assert.equal(environment.windowObject.__thermalWatchGoogleMapsReady, undefined);
  assert.equal(environment.timeouts.size, 0);

  const secondAttempt = loader.load("browser-key");
  assert.equal(environment.scripts.length, 2);
  environment.windowObject.google = { maps: {} };
  environment.windowObject.__thermalWatchGoogleMapsReady();
  await secondAttempt;
});

test("Google loader times out a stalled request and permits retry", async () => {
  const environment = googleEnvironment();
  const loader = createLoader(environment);
  const firstAttempt = loader.load("browser-key");
  environment.runTimeout();

  await assert.rejects(firstAttempt, /did not load within 15 seconds/);
  assert.equal(environment.scripts[0].removed, true);
  assert.equal(environment.windowObject.__thermalWatchGoogleMapsReady, undefined);

  const secondAttempt = loader.load("browser-key");
  assert.equal(environment.scripts.length, 2);
  environment.scripts[1].dispatch("error");
  await assert.rejects(secondAttempt, /could not be downloaded/);
});

test("Google authentication failure preserves the prior hook and rejects loading", async () => {
  const environment = googleEnvironment();
  let priorCalls = 0;
  let subscriberCalls = 0;
  environment.windowObject.gm_authFailure = () => { priorCalls += 1; };
  const loader = createLoader(environment);
  const unsubscribe = loader.subscribeToAuthenticationFailure(() => {
    subscriberCalls += 1;
  });
  const loading = loader.load("rejected-key");

  environment.windowObject.gm_authFailure();
  await assert.rejects(loading, /rejected the configured API key/);
  assert.equal(priorCalls, 1);
  assert.equal(subscriberCalls, 1);
  assert.equal(environment.scripts[0].removed, true);
  assert.equal(environment.windowObject.__thermalWatchGoogleMapsReady, undefined);

  unsubscribe();
  environment.windowObject.gm_authFailure();
  assert.equal(priorCalls, 2);
  assert.equal(subscriberCalls, 1);
  await assert.rejects(loader.load("rejected-key"), /rejected the configured API key/);
});
