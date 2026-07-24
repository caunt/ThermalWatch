"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const {
  imageryCoverageHeader,
  gibsTileApiUrl,
  googleMapsUrl,
  yandexMapsUrl,
  validateNearbyFeatures,
  parseCoordinateInput,
  coordinateSearchFromUrl,
  urlWithCoordinateSearch,
  nearestCoordinatePoint,
  notificationMarkerStyle,
  coordinateSearchMarkerStyle,
  clusterPointKeys,
  changedNotificationMarkerKeys,
  createGoogleAnomalyLayer,
  createMapResizeScheduler,
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

class FakeGooglePoint {
  constructor(coordinate) {
    this.coordinate = coordinate;
  }
}

class FakeGoogleFeature {
  constructor(options) {
    this.options = options;
  }

  getId() {
    return this.options.id;
  }

  getProperty(name) {
    return this.options.properties[name];
  }
}

class FakeGoogleDataLayer {
  static Feature = FakeGoogleFeature;
  static Point = FakeGooglePoint;
  static instances = [];

  constructor(options) {
    this.map = options.map;
    this.features = [];
    this.overrides = [];
    this.reverted = [];
    this.listeners = new Map();
    this.style = null;
    FakeGoogleDataLayer.instances.push(this);
  }

  setStyle(style) {
    this.style = style;
  }

  addListener(name, handler) {
    this.listeners.set(name, handler);
    return {
      remove: () => this.listeners.delete(name)
    };
  }

  add(feature) {
    this.features.push(feature);
  }

  forEach(callback) {
    this.features.forEach(callback);
  }

  remove(feature) {
    this.features.splice(this.features.indexOf(feature), 1);
  }

  overrideStyle(feature, style) {
    this.overrides.push({ feature, style });
  }

  revertStyle(feature) {
    this.reverted.push(feature);
  }

  setMap(map) {
    this.map = map;
  }

  click(feature) {
    this.listeners.get("click")?.({ feature });
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

test("notification cluster markers distinguish selected, clustered, and unrelated points", () => {
  assert.deepEqual(notificationMarkerStyle(true, true), {
    fill: "#ffd166", stroke: "#ffffff", size: 9, weight: 2
  });
  assert.deepEqual(notificationMarkerStyle(false, true), {
    fill: "#57d5ff", stroke: "#e9fbff", size: 8, weight: 2
  });
  assert.deepEqual(notificationMarkerStyle(false, false), {
    fill: "#ff593d", stroke: "#ffffff", size: 7, weight: 2
  });
});

test("coordinate search markers use one provider-neutral style", () => {
  assert.deepEqual(coordinateSearchMarkerStyle(), {
    fill: "#c084fc", stroke: "#ffffff", size: 12, weight: 3
  });
});

test("notification cluster member IDs map to provider-neutral point keys", () => {
  const points = [
    { key: "first", anomaly: { id: "a" } },
    { key: "duplicate#2", anomaly: { id: "a" } },
    { key: "unrelated", anomaly: { id: "b" } }
  ];

  assert.deepEqual([...clusterPointKeys(points, ["a"])], ["first", "duplicate#2"]);
  assert.throws(() => clusterPointKeys(null, ["a"]), /must be arrays/);
});

test("notification marker changes include only keys whose visual role changed", () => {
  assert.deepEqual(
    [...changedNotificationMarkerKeys(
      "selected",
      new Set(["selected", "unchanged-cluster", "leaving-cluster"]),
      "new-selected",
      new Set(["new-selected", "unchanged-cluster", "joining-cluster"]))],
    ["selected", "leaving-cluster", "new-selected", "joining-cluster"]);

  assert.deepEqual(
    [...changedNotificationMarkerKeys(null, new Set(), null, new Set())],
    []);
  assert.throws(
    () => changedNotificationMarkerKeys(null, [], null, new Set()),
    /must be sets/);
});

test("Google anomalies share one data layer and update only changed feature roles", () => {
  FakeGoogleDataLayer.instances = [];
  const selected = [];
  const map = { name: "map" };
  const anomalyLayer = createGoogleAnomalyLayer(map, {
    googleMaps: { Data: FakeGoogleDataLayer },
    onSelect: key => selected.push(key),
    labelForPoint: point => point.label,
    iconForState: (isSelected, isClustered) =>
      isSelected ? "selected" : isClustered ? "clustered" : "default"
  });
  const points = Array.from({ length: 9000 }, (_, index) => ({
    key: index === 8999 ? "point#2" : `point-${index}`,
    label: `Point ${index}`,
    latitude: 44 + index / 10000,
    longitude: 22 + index / 10000
  }));

  anomalyLayer.render(points);

  assert.equal(FakeGoogleDataLayer.instances.length, 1);
  const dataLayer = FakeGoogleDataLayer.instances[0];
  assert.equal(dataLayer.map, map);
  assert.equal(dataLayer.features.length, 9000);
  assert.equal(dataLayer.features[8999].getId(), "point#2");
  assert.deepEqual(dataLayer.features[0].options.geometry.coordinate, { lat: 44, lng: 22 });
  assert.deepEqual(dataLayer.style(dataLayer.features[0]), {
    clickable: true,
    cursor: "pointer",
    icon: "default",
    title: "Point 0"
  });

  dataLayer.click(dataLayer.features[8999]);
  assert.deepEqual(selected, ["point#2"]);

  anomalyLayer.setSelection(
    "point-0",
    new Set(["point-0", "point#2"]));
  assert.deepEqual(
    dataLayer.overrides.map(change => [change.feature.getId(), change.style]),
    [
      ["point-0", { icon: "selected", zIndex: 1000 }],
      ["point#2", { icon: "clustered", zIndex: 500 }]
    ]);

  anomalyLayer.setSelection(
    "point-1",
    new Set(["point-1", "point#2"]));
  assert.deepEqual(dataLayer.reverted.map(feature => feature.getId()), ["point-0"]);
  assert.deepEqual(
    dataLayer.overrides.slice(2).map(change => [change.feature.getId(), change.style]),
    [["point-1", { icon: "selected", zIndex: 1000 }]]);

  anomalyLayer.render([{ key: "replacement", label: "Replacement", latitude: 1, longitude: 2 }]);
  assert.equal(FakeGoogleDataLayer.instances.length, 1);
  assert.deepEqual(dataLayer.features.map(feature => feature.getId()), ["replacement"]);

  anomalyLayer.destroy();
  assert.equal(dataLayer.map, null);
  assert.equal(dataLayer.features.length, 0);
  assert.equal(dataLayer.listeners.size, 0);
  assert.throws(() => anomalyLayer.render([]), /has been destroyed/);
});

test("map resize scheduling coalesces callbacks to one per animation frame", () => {
  const frames = [];
  let resizeCount = 0;
  const scheduleResize = createMapResizeScheduler(
    () => { resizeCount += 1; },
    { requestAnimationFrameFunction: callback => frames.push(callback) });

  scheduleResize();
  scheduleResize();
  scheduleResize();

  assert.equal(frames.length, 1);
  assert.equal(resizeCount, 0);
  frames.shift()();
  assert.equal(resizeCount, 1);

  scheduleResize();
  assert.equal(frames.length, 1);
});

test("Yandex Maps URLs pin validated coordinates and open the satellite layer", () => {
  const url = new URL(yandexMapsUrl(50.123456, 30.654321));

  assert.equal(url.origin, "https://yandex.com");
  assert.equal(url.pathname, "/maps/");
  assert.equal(url.searchParams.get("ll"), "30.654321,50.123456");
  assert.equal(url.searchParams.get("pt"), "30.654321,50.123456");
  assert.equal(url.searchParams.get("z"), "12");
  assert.equal(url.searchParams.get("l"), "sat");
});

test("Yandex Maps URLs reject malformed and out-of-range coordinates", () => {
  assert.throws(() => yandexMapsUrl(Number.NaN, 30), /Valid map coordinates/);
  assert.throws(() => yandexMapsUrl(91, 30), /Valid map coordinates/);
  assert.throws(() => yandexMapsUrl(50, -181), /Valid map coordinates/);
});

test("Google Maps URLs point to the exact validated coordinates", () => {
  const url = new URL(googleMapsUrl(50.123456, 30.654321));

  assert.equal(url.origin, "https://www.google.com");
  assert.equal(url.pathname, "/maps/search/");
  assert.equal(url.searchParams.get("api"), "1");
  assert.equal(url.searchParams.get("query"), "50.123456,30.654321");
});

test("Google Maps URLs reject malformed and out-of-range coordinates", () => {
  assert.throws(() => googleMapsUrl(Number.NaN, 30), /Valid map coordinates/);
  assert.throws(() => googleMapsUrl(91, 30), /Valid map coordinates/);
  assert.throws(() => googleMapsUrl(50, -181), /Valid map coordinates/);
});

test("nearby feature diagnostics accept only bounded canonical OpenStreetMap results", () => {
  const feature = {
    osmType: "way",
    osmId: 123,
    name: "Factory & Sons",
    latitude: 50.1,
    longitude: 30.2,
    distanceKilometers: 1.25,
    openStreetMapUrl: "https://www.openstreetmap.org/way/123"
  };

  assert.deepEqual(validateNearbyFeatures([feature]), [feature]);
  assert.throws(() => validateNearbyFeatures(null), /must be an array/);
  assert.throws(
    () => validateNearbyFeatures([{ ...feature, distanceKilometers: 2.1 }]),
    /nearby feature is invalid/);
  assert.throws(
    () => validateNearbyFeatures([{
      ...feature,
      openStreetMapUrl: "https://example.test/way/123"
    }]),
    /nearby feature is invalid/);
  assert.throws(
    () => validateNearbyFeatures([{
      ...feature,
      openStreetMapUrl: "https://www.openstreetmap.org/way/123?redirect=example.test"
    }]),
    /nearby feature is invalid/);
});

test("coordinate search accepts decimal pairs and defaults ambiguous pairs to latitude first", () => {
  const expected = { latitude: 57.94608, longitude: 60.06142 };

  assert.deepEqual(parseCoordinateInput("57.946080, 60.061420"), expected);
  assert.deepEqual(parseCoordinateInput("57.946080 60.061420"), expected);
  assert.deepEqual(parseCoordinateInput("(57.946080; 60.061420)"), expected);
  assert.deepEqual(parseCoordinateInput("[57.946080 / 60.061420]"), expected);
  assert.deepEqual(parseCoordinateInput("57,946080 60,061420"), expected);
  assert.deepEqual(parseCoordinateInput("57,946080, 60,061420"), expected);
  assert.deepEqual(parseCoordinateInput("120 57"), { latitude: 57, longitude: 120 });
  assert.deepEqual(parseCoordinateInput("60 57"), { latitude: 60, longitude: 57 });
});

test("viewer URLs restore one validated latitude and longitude", () => {
  assert.equal(coordinateSearchFromUrl("https://viewer.example/"), null);
  assert.deepEqual(
    coordinateSearchFromUrl("https://viewer.example/?lat=57.946080&lon=60.061420"),
    { latitude: 57.94608, longitude: 60.06142 });
  assert.deepEqual(
    coordinateSearchFromUrl("/?lat=-0&lon=-0"),
    { latitude: 0, longitude: 0 });

  assert.throws(
    () => coordinateSearchFromUrl("/?lat=57"),
    /one lat and one lon/);
  assert.throws(
    () => coordinateSearchFromUrl("/?lat=57&lat=58&lon=60"),
    /one lat and one lon/);
  assert.throws(
    () => coordinateSearchFromUrl("/?lat=north&lon=60"),
    /not numeric/);
  assert.throws(
    () => coordinateSearchFromUrl("/?lat=91&lon=60"),
    /Latitude must be between/);
  assert.throws(
    () => coordinateSearchFromUrl("/?lat=57&lon=181"),
    /Longitude must be between/);
});

test("viewer URLs save canonical coordinates without discarding other URL state", () => {
  assert.equal(
    urlWithCoordinateSearch(
      "https://viewer.example/viewer?mode=compact#details",
      { latitude: 57.9460804, longitude: 60.0614206 }),
    "/viewer?mode=compact&lat=57.946080&lon=60.061421#details");
  assert.equal(
    urlWithCoordinateSearch(
      "/?lat=1&lon=2",
      { latitude: -0, longitude: -0 }),
    "/?lat=0.000000&lon=0.000000");

  assert.throws(
    () => urlWithCoordinateSearch("/", { latitude: Number.NaN, longitude: 60 }),
    /Latitude must be between/);
  assert.throws(
    () => urlWithCoordinateSearch("/", { latitude: 57, longitude: -181 }),
    /Longitude must be between/);
});

test("coordinate search accepts labels, cardinal directions, and common angle notations", () => {
  const expected = { latitude: 57.94608, longitude: 60.06142 };
  const approximateExpected = coordinate => {
    assert.ok(Math.abs(coordinate.latitude - expected.latitude) < 1e-10);
    assert.ok(Math.abs(coordinate.longitude - expected.longitude) < 1e-10);
  };

  assert.deepEqual(parseCoordinateInput("lat: 57.946080 lon: 60.061420"), expected);
  assert.deepEqual(parseCoordinateInput("60.061420 E, 57.946080 N"), expected);
  assert.deepEqual(parseCoordinateInput("N 57.946080 E 60.061420"), expected);
  assert.deepEqual(parseCoordinateInput("57.946080 degrees North, 60.061420 degrees East"), expected);
  approximateExpected(parseCoordinateInput("57°56′45.888″N 60°03′41.112″E"));
  approximateExpected(parseCoordinateInput("57° 56.7648' N, 60° 3.6852' E"));
  approximateExpected(parseCoordinateInput("57d56m45.888s, 60d3m41.112s"));
  approximateExpected(parseCoordinateInput("N 57 56 45.888 E 60 03 41.112"));
  approximateExpected(parseCoordinateInput("57:56:45.888 N, 60:03:41.112 E"));
});

test("coordinate search accepts geographic machine-readable formats", () => {
  const expected = { latitude: 57.94608, longitude: 60.06142 };

  assert.deepEqual(parseCoordinateInput("geo:57.946080,60.061420;u=10"), expected);
  assert.deepEqual(parseCoordinateInput("POINT (60.061420 57.946080)"), expected);
  assert.deepEqual(
    parseCoordinateInput('{"type":"Point","coordinates":[60.06142,57.94608]}'),
    expected);
  assert.deepEqual(
    parseCoordinateInput('{"type":"Feature","geometry":{"type":"Point","coordinates":[60.06142,57.94608]}}'),
    expected);
});

test("coordinate search extracts embedded Google Maps and Google Earth coordinates", () => {
  const expected = { latitude: 57.94608, longitude: 60.06142 };

  assert.deepEqual(
    parseCoordinateInput("https://www.google.com/maps/@57.946080,60.061420,12z"),
    expected);
  assert.deepEqual(
    parseCoordinateInput("www.google.co.uk/maps/search/?api=1&query=57.946080%2C60.061420"),
    expected);
  assert.deepEqual(
    parseCoordinateInput("https://earth.google.com/web/@57.946080,60.061420,1000a"),
    expected);
  assert.deepEqual(
    parseCoordinateInput(
      "https://www.google.com/maps/place/example/@1,2,3z/data=!4m6!3m5!8m2!3d57.946080!4d60.061420"),
    expected);
  assert.deepEqual(
    parseCoordinateInput(
      "https://www.google.com/maps/@1,2,3z?query=57.946080%2C60.061420"),
    expected);
});

test("coordinate search extracts embedded OpenStreetMap, Bing, and Yandex coordinates", () => {
  const expected = { latitude: 57.94608, longitude: 60.06142 };

  assert.deepEqual(
    parseCoordinateInput("https://www.openstreetmap.org/?mlat=57.946080&mlon=60.061420#map=12/1/2"),
    expected);
  assert.deepEqual(
    parseCoordinateInput("https://www.openstreetmap.org/#map=12/57.946080/60.061420&layers=N"),
    expected);
  assert.deepEqual(
    parseCoordinateInput("https://www.bing.com/maps?cp=57.946080~60.061420"),
    expected);
  assert.deepEqual(
    parseCoordinateInput("https://www.bing.com/maps?sp=point.57.946080_60.061420_example"),
    expected);
  assert.deepEqual(
    parseCoordinateInput("https://yandex.com/maps/?ll=60.061420%2C57.946080"),
    expected);
});

test("coordinate search rejects ambiguous, contradictory, unsupported, and out-of-range input", () => {
  assert.throws(() => parseCoordinateInput(""), /Enter a latitude/);
  assert.throws(() => parseCoordinateInput("57 60 10"), /not recognized/);
  assert.throws(() => parseCoordinateInput("181, 60"), /not recognized/);
  assert.throws(() => parseCoordinateInput("57°60'N 60°0'E"), /not recognized/);
  assert.throws(() => parseCoordinateInput("lat: 57 E, lon: 60 N"), /not recognized/);
  assert.throws(() => parseCoordinateInput("-57 N, 60 E"), /not recognized/);
  assert.throws(() => parseCoordinateInput("https://maps.app.goo.gl/example"), /Short Google Maps links/);
  assert.throws(() => parseCoordinateInput("https://www.google.com/maps/place/example"), /does not contain/);
  assert.throws(() => parseCoordinateInput("https://example.com/maps/@57,60"), /not supported/);
  assert.throws(() => parseCoordinateInput('{"type":"LineString","coordinates":[]}'), /GeoJSON Point/);
});

test("nearest coordinate search uses great-circle distance and deterministic input order", () => {
  const first = { key: "first", latitude: 57.94608, longitude: 60.06142 };
  const tied = { key: "tied", latitude: 57.94608, longitude: 60.06142 };
  const distant = { key: "distant", latitude: 0, longitude: 0 };
  const nearest = nearestCoordinatePoint(
    [distant, first, tied],
    { latitude: 57.94608, longitude: 60.06142 });

  assert.equal(nearest.point, first);
  assert.equal(nearest.distanceKilometers, 0);
  assert.equal(nearestCoordinatePoint([], { latitude: 0, longitude: 0 }), null);
  assert.equal(
    nearestCoordinatePoint(
      [{ key: "east", latitude: 0, longitude: 179.9 }, { key: "west", latitude: 0, longitude: -170 }],
      { latitude: 0, longitude: -179.9 }).point.key,
    "east");
  assert.throws(() => nearestCoordinatePoint(null, { latitude: 0, longitude: 0 }), /must be an array/);
  assert.throws(() => nearestCoordinatePoint([], { latitude: 91, longitude: 0 }), /Latitude/);
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
