"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const {
  gibsProducts,
  gibsTileUrl,
  loadGibsTile,
  createGibsWarningReporter,
  createGoogleMapsLoader
} = require("../src/ThermalWatch.Api/wwwroot/map-support.js");

class FakeImage {
  constructor() {
    this.onload = null;
    this.onerror = null;
    this.requests = [];
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

test("GIBS products prefer Terra and contain only current corrected-reflectance sources", () => {
  assert.deepEqual(
    gibsProducts.map(product => product.id),
    ["modis-terra", "modis-aqua", "viirs-noaa21", "viirs-noaa20", "viirs-snpp"]);
  assert.ok(gibsProducts.every(product =>
    product.layer.endsWith("_CorrectedReflectance_TrueColor")));
  assert.ok(gibsProducts.every(product => !product.layer.includes("BlueMarble")));
});

test("GIBS tile URLs request each product's latest default date", () => {
  assert.equal(
    gibsTileUrl(gibsProducts[0], { z: 6, y: 21, x: 37 }),
    "https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/"
      + "MODIS_Terra_CorrectedReflectance_TrueColor/default/default/"
      + "GoogleMapsCompatible_Level9/6/21/37.jpg");
});

test("GIBS tile loading stops after Terra succeeds", () => {
  const image = new FakeImage();
  let completed = null;

  loadGibsTile(image, { z: 3, y: 2, x: 1 }, {
    onComplete: result => { completed = result; }
  });
  assert.equal(image.requests.length, 1);
  assert.match(image.requests[0], /MODIS_Terra/);

  image.succeed();
  assert.equal(completed.available, true);
  assert.equal(completed.product.id, "modis-terra");
  assert.equal(completed.productIndex, 0);
  assert.equal(image.requests.length, 1);
});

test("GIBS tile loading falls back serially through the configured satellites", () => {
  const image = new FakeImage();
  let completed = null;

  loadGibsTile(image, { z: 3, y: 2, x: 1 }, {
    onComplete: result => { completed = result; }
  });
  image.fail();
  assert.match(image.requests[1], /MODIS_Aqua/);
  image.fail();
  assert.match(image.requests[2], /VIIRS_NOAA21/);
  image.succeed();

  assert.equal(completed.available, true);
  assert.equal(completed.product.id, "viirs-noaa21");
  assert.equal(completed.productIndex, 2);
  assert.equal(image.requests.length, 3);
});

test("GIBS tile loading leaves a blank tile after every current source fails", () => {
  const image = new FakeImage();
  let completed = null;

  loadGibsTile(image, { z: 3, y: 2, x: 1 }, {
    onComplete: result => { completed = result; }
  });
  for (let index = 0; index < gibsProducts.length; index += 1)
    image.fail();

  assert.equal(completed.available, false);
  assert.equal(completed.product, null);
  assert.deepEqual(
    image.requests.slice(0, gibsProducts.length).map(url =>
      gibsProducts.find(product => url.includes(product.layer)).id),
    gibsProducts.map(product => product.id));
  assert.match(image.requests.at(-1), /^data:image\/gif;base64,/);
});

test("GIBS warnings are deduplicated and exhaustion remains the strongest state", () => {
  const warnings = [];
  const report = createGibsWarningReporter(message => warnings.push(message));
  const fallback = { available: true, product: gibsProducts[1], productIndex: 1 };
  const exhausted = { available: false, product: null, productIndex: -1 };

  report(fallback);
  report(fallback);
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /Aqua or VIIRS/);

  report(exhausted);
  report(exhausted);
  report(fallback);
  assert.equal(warnings.length, 2);
  assert.match(warnings[1], /left blank/);
  assert.doesNotMatch(warnings.join(" "), /Blue Marble/i);
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
