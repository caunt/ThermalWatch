"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const {
  gibsProducts,
  gibsTileSize,
  gibsNoDataMaximum,
  gibsTileUrl,
  isGibsNoDataPixel,
  mergeGibsPixels,
  loadGibsTile,
  createGibsWarningReporter,
  createGoogleMapsLoader
} = require("../src/ThermalWatch.Api/wwwroot/map-support.js");

class FakeImage {
  constructor() {
    this.onload = null;
    this.onerror = null;
    this.requests = [];
    this.pixels = null;
    this.sourceRemoved = false;
  }

  set src(value) {
    this.requests.push(value);
  }

  succeed(pixels) {
    this.pixels = pixels;
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

const gibsPixelCount = gibsTileSize * gibsTileSize;

function pixelsWith(red, green, blue, alpha = 255) {
  const pixels = new Uint8ClampedArray(gibsPixelCount * 4);
  for (let offset = 0; offset < pixels.length; offset += 4) {
    pixels[offset] = red;
    pixels[offset + 1] = green;
    pixels[offset + 2] = blue;
    pixels[offset + 3] = alpha;
  }
  return pixels;
}

function setPixel(pixels, index, red, green, blue, alpha = 255) {
  const offset = index * 4;
  pixels[offset] = red;
  pixels[offset + 1] = green;
  pixels[offset + 2] = blue;
  pixels[offset + 3] = alpha;
}

function gibsComposition() {
  const images = [];
  const canvas = { width: 0, height: 0, pixels: null };
  let result = null;
  const cancel = loadGibsTile(canvas, { z: 3, y: 2, x: 1 }, {
    createImage: () => {
      const image = new FakeImage();
      images.push(image);
      return image;
    },
    readPixels: image => {
      if (!image.pixels)
        throw new Error("The fake image has no readable pixels.");
      return image.pixels;
    },
    writePixels: (target, pixels) => { target.pixels = pixels.slice(); },
    onComplete: value => { result = value; }
  });

  return { canvas, images, cancel, result: () => result };
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
      + "GoogleMapsCompatible_Level9/6/21/37.jpeg");
});

test("GIBS no-data detection recognizes transparent and near-black JPEG pixels", () => {
  const pixels = new Uint8ClampedArray([
    0, 0, 0, 255,
    gibsNoDataMaximum, gibsNoDataMaximum, gibsNoDataMaximum, 255,
    gibsNoDataMaximum + 1, gibsNoDataMaximum, gibsNoDataMaximum, 255,
    80, 90, 100, 0
  ]);

  assert.equal(isGibsNoDataPixel(pixels, 0), true);
  assert.equal(isGibsNoDataPixel(pixels, 4), true);
  assert.equal(isGibsNoDataPixel(pixels, 8), false);
  assert.equal(isGibsNoDataPixel(pixels, 12), true);
});

test("GIBS pixel merging fills only destination holes", () => {
  const destination = new Uint8ClampedArray([
    90, 91, 92, 255,
    0, 0, 0, 0,
    0, 0, 0, 0
  ]);
  const source = new Uint8ClampedArray([
    140, 141, 142, 255,
    60, 61, 62, 255,
    3, 4, 5, 255
  ]);

  assert.equal(mergeGibsPixels(destination, source), 1);
  assert.deepEqual(
    [...destination],
    [90, 91, 92, 255, 60, 61, 62, 255, 0, 0, 0, 0]);
});

test("GIBS compositing stops after complete Terra coverage", () => {
  const composition = gibsComposition();
  assert.equal(composition.images.length, 1);
  assert.match(composition.images[0].requests[0], /MODIS_Terra/);

  composition.images[0].succeed(pixelsWith(40, 70, 110));

  assert.equal(composition.images.length, 1);
  assert.equal(composition.result().available, true);
  assert.equal(composition.result().complete, true);
  assert.equal(composition.result().unresolvedPixels, 0);
  assert.deepEqual(
    composition.result().usedProducts.map(entry => entry.product.id),
    ["modis-terra"]);
  assert.deepEqual([...composition.canvas.pixels.slice(0, 4)], [40, 70, 110, 255]);
});

test("GIBS compositing fills Terra no-data pixels from Aqua without replacing Terra", () => {
  const composition = gibsComposition();
  const terra = pixelsWith(42, 72, 112);
  setPixel(terra, 17, 0, 0, 0);
  composition.images[0].succeed(terra);

  assert.equal(composition.images.length, 2);
  assert.match(composition.images[1].requests[0], /MODIS_Aqua/);
  composition.images[1].succeed(pixelsWith(82, 92, 102));

  const result = composition.result();
  assert.equal(result.complete, true);
  assert.deepEqual(
    result.usedProducts.map(entry => [entry.product.id, entry.filledPixels]),
    [["modis-terra", gibsPixelCount - 1], ["modis-aqua", 1]]);
  assert.deepEqual([...composition.canvas.pixels.slice(0, 4)], [42, 72, 112, 255]);
  assert.deepEqual(
    [...composition.canvas.pixels.slice(17 * 4, (17 * 4) + 4)],
    [82, 92, 102, 255]);
});

test("GIBS compositing continues after request and pixel-read failures", () => {
  const composition = gibsComposition();
  composition.images[0].fail();
  assert.match(composition.images[1].requests[0], /MODIS_Aqua/);

  composition.images[1].succeed(null);
  assert.match(composition.images[2].requests[0], /VIIRS_NOAA21/);
  composition.images[2].succeed(pixelsWith(55, 85, 115));

  assert.equal(composition.result().complete, true);
  assert.deepEqual(
    composition.result().usedProducts.map(entry => entry.product.id),
    ["viirs-noaa21"]);
});

test("GIBS compositing leaves unresolved pixels transparent after every source", () => {
  const composition = gibsComposition();
  for (let index = 0; index < gibsProducts.length; index += 1)
    composition.images[index].succeed(pixelsWith(0, 0, 0));

  const result = composition.result();
  assert.equal(result.available, false);
  assert.equal(result.complete, false);
  assert.equal(result.unresolvedPixels, gibsPixelCount);
  assert.deepEqual(result.usedProducts, []);
  assert.ok(composition.canvas.pixels.every(value => value === 0));
  assert.deepEqual(
    composition.images.map(image =>
      gibsProducts.find(product => image.requests[0].includes(product.layer)).id),
    gibsProducts.map(product => product.id));
});

test("GIBS tile cancellation ignores late image completion", () => {
  const composition = gibsComposition();
  composition.cancel();
  assert.equal(composition.images[0].sourceRemoved, true);

  composition.images[0].succeed(pixelsWith(60, 70, 80));
  assert.equal(composition.images.length, 1);
  assert.equal(composition.result(), null);
  assert.equal(composition.canvas.pixels, null);
});

test("GIBS warnings ignore successful supplementation and deduplicate unresolved coverage", () => {
  const warnings = [];
  const report = createGibsWarningReporter(message => warnings.push(message));
  const complete = { complete: true, unresolvedPixels: 0 };
  const unresolved = { complete: false, unresolvedPixels: 27 };

  report(complete);
  report(complete);
  assert.deepEqual(warnings, []);

  report(unresolved);
  report(unresolved);
  report(complete);
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /neutral map background/);
  assert.doesNotMatch(warnings[0], /historical|fallback/i);
});

test("Runtime viewer assets contain no Blue Marble behavior or warning", () => {
  const viewerRoot = join(__dirname, "../src/ThermalWatch.Api/wwwroot");
  const runtime = ["index.html", "app.js", "map-support.js"]
    .map(file => readFileSync(join(viewerRoot, file), "utf8"))
    .join("\n");

  assert.doesNotMatch(runtime, /Blue Marble|BlueMarble/i);
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
