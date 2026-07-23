(function (root, factory) {
  "use strict";

  const support = factory();
  if (typeof module === "object" && module.exports)
    module.exports = support;
  else if (root)
    root.ThermalWatchMapSupport = support;
})(typeof globalThis === "object" ? globalThis : this, () => {
  "use strict";

  const imageryCoverageHeader = "X-ThermalWatch-Imagery-Coverage";
  const imageryCoverageValues = new Set(["complete", "partial", "none"]);
  const coordinateNumberPattern = "[+-]?(?:\\d+(?:[.,]\\d+)?|[.,]\\d+)";
  const urlCoordinateNumberPattern = "[+-]?(?:\\d+(?:\\.\\d+)?|\\.\\d+)";

  function gibsTileApiUrl(coordinates) {
    if (!coordinates
        || !Number.isInteger(coordinates.x)
        || !Number.isInteger(coordinates.y)
        || !Number.isInteger(coordinates.z)) {
      throw new Error("Integer map tile coordinates are required.");
    }

    return `/api/viewer/imagery/gibs/${coordinates.z}/${coordinates.x}/${coordinates.y}.png`;
  }

  function yandexMapsUrl(latitude, longitude) {
    if (!Number.isFinite(latitude)
        || latitude < -90
        || latitude > 90
        || !Number.isFinite(longitude)
        || longitude < -180
        || longitude > 180) {
      throw new Error("Valid map coordinates are required.");
    }

    const coordinates = `${longitude},${latitude}`;
    const url = new URL("https://yandex.com/maps/");
    url.searchParams.set("ll", coordinates);
    url.searchParams.set("pt", coordinates);
    url.searchParams.set("z", "12");
    url.searchParams.set("l", "sat");
    return url.href;
  }

  function validateNearbyFeatures(features) {
    if (!Array.isArray(features))
      throw new Error("Nearby features must be an array.");

    features.forEach(feature => {
      if (!feature
          || !["node", "way", "relation"].includes(feature.osmType)
          || !Number.isSafeInteger(feature.osmId)
          || feature.osmId <= 0
          || typeof feature.name !== "string"
          || feature.name.trim().length === 0
          || !Number.isFinite(feature.latitude)
          || feature.latitude < -90
          || feature.latitude > 90
          || !Number.isFinite(feature.longitude)
          || feature.longitude < -180
          || feature.longitude > 180
          || !Number.isFinite(feature.distanceKilometers)
          || feature.distanceKilometers < 0
          || feature.distanceKilometers > 2.000001
          || !isCanonicalOpenStreetMapUrl(feature)) {
        throw new Error("A nearby feature is invalid.");
      }
    });

    return features;
  }

  function isCanonicalOpenStreetMapUrl(feature) {
    if (typeof feature.openStreetMapUrl !== "string")
      return false;

    try {
      const url = new URL(feature.openStreetMapUrl);
      return url.protocol === "https:"
        && url.hostname === "www.openstreetmap.org"
        && url.port === ""
        && url.pathname === `/${feature.osmType}/${feature.osmId}`
        && url.search === ""
        && url.hash === "";
    } catch {
      return false;
    }
  }

  function parseCoordinateInput(input) {
    if (typeof input !== "string" || input.trim().length === 0)
      throw new Error("Enter a latitude and longitude or a coordinate-bearing map link.");

    const value = input.trim();
    if (value.length > 4096)
      throw new Error("The coordinate input is too long.");

    const geoCoordinate = parseGeoUri(value);
    if (geoCoordinate)
      return geoCoordinate;

    const urlCoordinate = parseCoordinateUrl(value);
    if (urlCoordinate)
      return urlCoordinate;

    const geoJsonCoordinate = parseGeoJsonPoint(value);
    if (geoJsonCoordinate)
      return geoJsonCoordinate;

    const wktCoordinate = parseWktPoint(value);
    if (wktCoordinate)
      return wktCoordinate;

    const pair = tryParseCoordinatePair(value);
    if (pair)
      return pair;

    throw new Error(
      "Coordinates were not recognized. Enter latitude then longitude, use compass labels, or paste a supported map link.");
  }

  function parseGeoUri(value) {
    const match = /^geo:([^?;]+)(?:[?;].*)?$/i.exec(value);
    if (!match)
      return null;

    const parts = match[1].split(",").map(part => part.trim());
    if (parts.length < 2 || parts.length > 3)
      throw new Error("The geo URI does not contain a valid latitude and longitude.");

    return createCoordinate(
      parseDecimalNumber(parts[0]),
      parseDecimalNumber(parts[1]));
  }

  function parseCoordinateUrl(value) {
    const candidate = /^[a-z][a-z\d+.-]*:\/\//i.test(value)
      ? value
      : looksLikeSupportedMapHost(value) ? `https://${value}` : null;
    if (!candidate)
      return null;

    let url;
    try {
      url = new URL(candidate);
    } catch {
      throw new Error("The pasted map link is not a valid URL.");
    }

    const hostname = url.hostname.toLowerCase();
    if (hostname === "maps.app.goo.gl" || hostname === "goo.gl") {
      throw new Error(
        "Short Google Maps links cannot be resolved. Paste the full Google Maps URL instead.");
    }

    if (isGoogleHost(hostname))
      return parseGoogleCoordinateUrl(url);
    if (/(^|\.)openstreetmap\.org$/.test(hostname))
      return parseOpenStreetMapCoordinateUrl(url);
    if (/(^|\.)bing\.com$/.test(hostname))
      return parseBingCoordinateUrl(url);
    if (/(^|\.)yandex\.[a-z.]+$/.test(hostname))
      return parseYandexCoordinateUrl(url);

    throw new Error("This map-link provider is not supported.");
  }

  function looksLikeSupportedMapHost(value) {
    return /^(?:(?:www|maps|earth)\.)?(?:google\.[a-z.]+|openstreetmap\.org|bing\.com|yandex\.[a-z.]+)\//i.test(value)
      || /^(?:maps\.app\.goo\.gl|goo\.gl)\//i.test(value);
  }

  function isGoogleHost(hostname) {
    return /(^|\.)google\.[a-z.]+$/.test(hostname);
  }

  function parseGoogleCoordinateUrl(url) {
    for (const name of ["query", "destination", "daddr", "q", "center", "ll", "saddr"]) {
      const parameter = url.searchParams.get(name);
      if (!parameter)
        continue;

      const coordinate = tryParseCoordinatePair(parameter.replace(/^loc:/i, ""));
      if (coordinate)
        return coordinate;
    }

    const decodedUrl = safelyDecodeUrlParts(url);
    const dataCoordinate = coordinateFromPattern(
      decodedUrl,
      new RegExp(`!3d(${urlCoordinateNumberPattern})!4d(${urlCoordinateNumberPattern})`, "i"),
      "latitude-longitude");
    if (dataCoordinate)
      return dataCoordinate;

    const centerCoordinate = coordinateFromPattern(
      decodedUrl,
      new RegExp(`@(${urlCoordinateNumberPattern}),(${urlCoordinateNumberPattern})(?:,|\\b)`, "i"),
      "latitude-longitude");
    if (centerCoordinate)
      return centerCoordinate;

    const placeCoordinate = coordinateFromPattern(
      decodedUrl,
      new RegExp(`/place/(?:[^/@]+/)?(${urlCoordinateNumberPattern}),(${urlCoordinateNumberPattern})(?:[/@?]|$)`, "i"),
      "latitude-longitude");
    if (placeCoordinate)
      return placeCoordinate;

    throw new Error("The Google Maps link does not contain embedded coordinates.");
  }

  function parseOpenStreetMapCoordinateUrl(url) {
    const markerLatitude = url.searchParams.get("mlat");
    const markerLongitude = url.searchParams.get("mlon");
    if (markerLatitude !== null && markerLongitude !== null) {
      return createCoordinate(
        parseDecimalNumber(markerLatitude),
        parseDecimalNumber(markerLongitude));
    }

    const match = /^#map=\d+(?:\.\d+)?\/([^/&#]+)\/([^/&#]+)/i.exec(url.hash);
    if (match) {
      return createCoordinate(
        parseDecimalNumber(match[1]),
        parseDecimalNumber(match[2]));
    }

    throw new Error("The OpenStreetMap link does not contain embedded coordinates.");
  }

  function parseBingCoordinateUrl(url) {
    const center = url.searchParams.get("cp");
    if (center) {
      const match = new RegExp(
        `^\\s*(${urlCoordinateNumberPattern})\\s*~\\s*(${urlCoordinateNumberPattern})\\s*$`).exec(center);
      if (match)
        return createCoordinate(parseDecimalNumber(match[1]), parseDecimalNumber(match[2]));
    }

    const point = url.searchParams.get("sp");
    if (point) {
      const match = new RegExp(
        `^point\\.(${urlCoordinateNumberPattern})_(${urlCoordinateNumberPattern})(?:_|$)`, "i").exec(point);
      if (match)
        return createCoordinate(parseDecimalNumber(match[1]), parseDecimalNumber(match[2]));
    }

    throw new Error("The Bing Maps link does not contain embedded coordinates.");
  }

  function parseYandexCoordinateUrl(url) {
    const center = url.searchParams.get("ll") ?? url.searchParams.get("pt");
    if (center) {
      const match = new RegExp(
        `^\\s*(${urlCoordinateNumberPattern})\\s*,\\s*(${urlCoordinateNumberPattern})(?:,.*)?$`).exec(center);
      if (match) {
        return createCoordinate(
          parseDecimalNumber(match[2]),
          parseDecimalNumber(match[1]));
      }
    }

    throw new Error("The Yandex Maps link does not contain embedded coordinates.");
  }

  function safelyDecodeUrlParts(url) {
    try {
      return decodeURIComponent(`${url.pathname}${url.search}${url.hash}`);
    } catch {
      return `${url.pathname}${url.search}${url.hash}`;
    }
  }

  function coordinateFromPattern(value, pattern, order) {
    const match = pattern.exec(value);
    if (!match)
      return null;

    const first = parseDecimalNumber(match[1]);
    const second = parseDecimalNumber(match[2]);
    return order === "longitude-latitude"
      ? createCoordinate(second, first)
      : createCoordinate(first, second);
  }

  function parseGeoJsonPoint(value) {
    if (!value.startsWith("{"))
      return null;

    let json;
    try {
      json = JSON.parse(value);
    } catch {
      throw new Error("The GeoJSON value is not valid JSON.");
    }

    const point = typeof json?.type === "string" && json.type.toLowerCase() === "feature"
      ? json.geometry
      : json;
    if (typeof point?.type !== "string"
        || point.type.toLowerCase() !== "point"
        || !Array.isArray(point.coordinates)
        || point.coordinates.length < 2
        || typeof point.coordinates[0] !== "number"
        || typeof point.coordinates[1] !== "number") {
      throw new Error("Expected a GeoJSON Point with numeric coordinates.");
    }

    return createCoordinate(point.coordinates[1], point.coordinates[0]);
  }

  function parseWktPoint(value) {
    const match = new RegExp(
      `^POINT(?:\\s+(?:Z|M|ZM))?\\s*\\(\\s*(${urlCoordinateNumberPattern})\\s+(${urlCoordinateNumberPattern})(?:\\s+${urlCoordinateNumberPattern})?\\s*\\)$`,
      "i").exec(value);
    if (!match)
      return null;

    return createCoordinate(parseDecimalNumber(match[2]), parseDecimalNumber(match[1]));
  }

  function tryParseCoordinatePair(value) {
    const unwrapped = unwrapCoordinatePair(value.trim());
    const candidates = new Map();
    const separators = /\s*;\s*|\s*\/\s*|\s*,\s*|\s+/g;
    let match;
    while ((match = separators.exec(unwrapped)) !== null) {
      const firstText = unwrapped.slice(0, match.index).trim();
      const secondText = unwrapped.slice(match.index + match[0].length).trim();
      if (!firstText || !secondText)
        continue;

      const coordinate = tryCreateCoordinatePair(firstText, secondText);
      if (coordinate)
        candidates.set(`${coordinate.latitude},${coordinate.longitude}`, coordinate);
    }

    return candidates.size === 1 ? candidates.values().next().value : null;
  }

  function unwrapCoordinatePair(value) {
    const wrappers = new Map([["(", ")"], ["[", "]"]]);
    const closing = wrappers.get(value[0]);
    return closing && value.at(-1) === closing ? value.slice(1, -1).trim() : value;
  }

  function tryCreateCoordinatePair(firstText, secondText) {
    try {
      const first = parseCoordinateAngle(firstText);
      const second = parseCoordinateAngle(secondText);
      if (first.axis && second.axis && first.axis === second.axis)
        return null;

      if (first.axis === "longitude" || second.axis === "latitude")
        return createCoordinate(second.value, first.value);
      if (first.axis === "latitude" || second.axis === "longitude")
        return createCoordinate(first.value, second.value);

      if (Math.abs(first.value) > 90 && Math.abs(first.value) <= 180 && Math.abs(second.value) <= 90)
        return createCoordinate(second.value, first.value);

      return createCoordinate(first.value, second.value);
    } catch {
      return null;
    }
  }

  function parseCoordinateAngle(value) {
    let text = value
      .replace(/[\u2212\u2012\u2013\u2014]/g, "-")
      .trim();
    let axis = null;
    let hemisphere = null;

    const label = /^(latitude|lat|longitude|lon|lng)\.?\s*[:=]?\s*/i.exec(text);
    if (label) {
      axis = /^lat/i.test(label[1]) ? "latitude" : "longitude";
      text = text.slice(label[0].length).trim();
    }

    const prefix = /^(north|south|east|west|[NSEW])(?=$|[\s+\-\d.,])\s*/i.exec(text);
    if (prefix) {
      hemisphere = prefix[1][0].toUpperCase();
      text = text.slice(prefix[0].length).trim();
    }

    const suffix = /\s*(north|south|east|west|[NSEW])$/i.exec(text);
    const suffixIsUnit = suffix?.[1] === "s" && /(?:°|º|\d\s*d).*\d\s*m/i.test(text);
    const suffixIsPartOfWord = suffix?.[1].length === 1
      && suffix.index > 0
      && /[A-Za-z]/.test(text[suffix.index - 1]);
    if (suffix && !suffixIsUnit && !suffixIsPartOfWord) {
      const suffixHemisphere = suffix[1][0].toUpperCase();
      if (hemisphere && hemisphere !== suffixHemisphere)
        throw new Error("A coordinate cannot use conflicting hemispheres.");
      hemisphere = suffixHemisphere;
      text = text.slice(0, suffix.index).trim();
    }

    const hemisphereAxis = hemisphere && /[NS]/.test(hemisphere) ? "latitude"
      : hemisphere ? "longitude"
        : null;
    if (axis && hemisphereAxis && axis !== hemisphereAxis)
      throw new Error("The coordinate label and hemisphere do not agree.");
    axis ??= hemisphereAxis;

    const angle = parseAngleMagnitude(text, Boolean(hemisphere));
    const signCharacter = angle.degreesText[0];
    if (hemisphere) {
      const negativeHemisphere = hemisphere === "S" || hemisphere === "W";
      if ((signCharacter === "+" && negativeHemisphere)
          || (signCharacter === "-" && !negativeHemisphere)) {
        throw new Error("The coordinate sign and hemisphere do not agree.");
      }

      angle.value = negativeHemisphere ? -Math.abs(angle.value) : Math.abs(angle.value);
    }

    return { axis, value: angle.value };
  }

  function parseAngleMagnitude(value, hasHemisphere) {
    const decimal = new RegExp(
      `^(${coordinateNumberPattern})\\s*(?:°|º|d(?:eg(?:rees?)?)?)?$`, "i").exec(value);
    if (decimal)
      return angleResult(decimal[1], null, null);

    const marked = new RegExp(
      `^(${coordinateNumberPattern})\\s*(?:°|º|d(?:eg(?:rees?)?)?)\\s*`
        + `(\\d+(?:[.,]\\d+)?)\\s*(?:['′’]|m(?:in(?:utes?)?)?)?\\s*`
        + `(?:(\\d+(?:[.,]\\d+)?)\\s*(?:["″”]|s(?:ec(?:onds?)?)?)?)?$`,
      "i").exec(value);
    if (marked)
      return angleResult(marked[1], marked[2], marked[3] ?? null);

    const colon = new RegExp(
      `^(${coordinateNumberPattern})\\s*:\\s*(\\d+(?:[.,]\\d+)?)`
        + `(?:\\s*:\\s*(\\d+(?:[.,]\\d+)?))?$`).exec(value);
    if (colon)
      return angleResult(colon[1], colon[2], colon[3] ?? null);

    if (hasHemisphere) {
      const unmarked = new RegExp(
        `^(${coordinateNumberPattern})\\s+(\\d+(?:[.,]\\d+)?)`
          + `(?:\\s+(\\d+(?:[.,]\\d+)?))?$`).exec(value);
      if (unmarked)
        return angleResult(unmarked[1], unmarked[2], unmarked[3] ?? null);
    }

    throw new Error("The coordinate angle is malformed.");
  }

  function angleResult(degreesText, minutesText, secondsText) {
    const degrees = parseDecimalNumber(degreesText);
    const minutes = minutesText === null ? 0 : parseDecimalNumber(minutesText);
    const seconds = secondsText === null ? 0 : parseDecimalNumber(secondsText);
    if (minutes < 0 || minutes >= 60 || seconds < 0 || seconds >= 60)
      throw new Error("Coordinate minutes and seconds must be less than 60.");

    const magnitude = Math.abs(degrees) + minutes / 60 + seconds / 3600;
    const value = degreesText.trim().startsWith("-") ? -magnitude : magnitude;
    return { degreesText: degreesText.trim(), value };
  }

  function parseDecimalNumber(value) {
    if (typeof value !== "string"
        || !new RegExp(`^${coordinateNumberPattern}$`).test(value.trim())) {
      throw new Error("A coordinate component is not numeric.");
    }

    const number = Number(value.trim().replace(",", "."));
    if (!Number.isFinite(number))
      throw new Error("A coordinate component is not finite.");
    return number;
  }

  function createCoordinate(latitude, longitude) {
    if (!Number.isFinite(latitude) || latitude < -90 || latitude > 90)
      throw new Error("Latitude must be between -90 and 90 degrees.");
    if (!Number.isFinite(longitude) || longitude < -180 || longitude > 180)
      throw new Error("Longitude must be between -180 and 180 degrees.");

    return Object.freeze({
      latitude: Object.is(latitude, -0) ? 0 : latitude,
      longitude: Object.is(longitude, -0) ? 0 : longitude
    });
  }

  function nearestCoordinatePoint(points, coordinate) {
    if (!Array.isArray(points))
      throw new Error("Coordinate points must be an array.");
    const target = createCoordinate(coordinate?.latitude, coordinate?.longitude);
    let nearest = null;

    points.forEach(point => {
      let pointCoordinate;
      try {
        pointCoordinate = createCoordinate(point?.latitude, point?.longitude);
      } catch {
        return;
      }

      const distanceKilometers = greatCircleDistanceKilometers(target, pointCoordinate);
      if (!nearest || distanceKilometers < nearest.distanceKilometers) {
        nearest = {
          point,
          distanceKilometers
        };
      }
    });

    return nearest ? Object.freeze(nearest) : null;
  }

  function greatCircleDistanceKilometers(first, second) {
    const toRadians = degrees => degrees * Math.PI / 180;
    const latitudeDelta = toRadians(second.latitude - first.latitude);
    const longitudeDelta = toRadians(second.longitude - first.longitude);
    const firstLatitude = toRadians(first.latitude);
    const secondLatitude = toRadians(second.latitude);
    const haversine = Math.sin(latitudeDelta / 2) ** 2
      + Math.cos(firstLatitude) * Math.cos(secondLatitude) * Math.sin(longitudeDelta / 2) ** 2;
    const boundedHaversine = Math.min(1, Math.max(0, haversine));
    return 6371.0088 * 2
      * Math.atan2(Math.sqrt(boundedHaversine), Math.sqrt(1 - boundedHaversine));
  }

  function notificationMarkerStyle(selected, clustered = false) {
    if (selected)
      return Object.freeze({ fill: "#ffd166", stroke: "#ffffff", size: 9, weight: 2 });
    if (clustered)
      return Object.freeze({ fill: "#57d5ff", stroke: "#e9fbff", size: 8, weight: 2 });
    return Object.freeze({ fill: "#ff593d", stroke: "#ffffff", size: 7, weight: 2 });
  }

  function coordinateSearchMarkerStyle() {
    return Object.freeze({ fill: "#c084fc", stroke: "#ffffff", size: 12, weight: 3 });
  }

  function clusterPointKeys(points, memberIds) {
    if (!Array.isArray(points) || !Array.isArray(memberIds))
      throw new Error("Points and notification member IDs must be arrays.");

    const members = new Set(memberIds.filter(id => typeof id === "string"));
    return new Set(points
      .filter(point => typeof point?.key === "string" && members.has(point.anomaly?.id))
      .map(point => point.key));
  }

  function createMapResizeScheduler(onResize, options = {}) {
    if (typeof onResize !== "function")
      throw new Error("A map resize callback is required.");

    const requestAnimationFrameFunction = options.requestAnimationFrameFunction
      ?? globalThis.requestAnimationFrame;
    if (typeof requestAnimationFrameFunction !== "function")
      throw new Error("Animation frames are required to schedule map resizing.");

    let framePending = false;
    return () => {
      if (framePending)
        return;

      framePending = true;
      requestAnimationFrameFunction(() => {
        framePending = false;
        onResize();
      });
    };
  }

  function loadGibsTile(image, coordinates, options = {}) {
    if (!image)
      throw new Error("An image element is required to load a map tile.");

    const fetchFunction = options.fetchFunction ?? globalThis.fetch;
    const createObjectUrl = options.createObjectUrl ?? (blob => globalThis.URL.createObjectURL(blob));
    const revokeObjectUrl = options.revokeObjectUrl ?? (url => globalThis.URL.revokeObjectURL(url));
    const createAbortController = options.createAbortController ?? (() => new globalThis.AbortController());
    const onComplete = options.onComplete ?? (() => {});
    const onError = options.onError ?? (() => {});
    const controller = createAbortController();
    let active = true;
    let objectUrl = null;

    const clearImageHandlers = () => {
      image.onload = null;
      image.onerror = null;
    };

    const releaseObjectUrl = () => {
      if (objectUrl) {
        revokeObjectUrl(objectUrl);
        objectUrl = null;
      }
    };

    void (async () => {
      try {
        const response = await fetchFunction(gibsTileApiUrl(coordinates), {
          cache: "default",
          headers: { Accept: "image/png" },
          signal: controller.signal
        });
        if (!response.ok)
          throw new Error(`The imagery API returned HTTP ${response.status}.`);

        const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim();
        if (mediaType?.toLowerCase() !== "image/png")
          throw new Error("The imagery API returned an unsupported response.");

        const coverageHeader = response.headers.get(imageryCoverageHeader)?.toLowerCase();
        const coverage = imageryCoverageValues.has(coverageHeader) ? coverageHeader : "none";
        const blob = await response.blob();
        if (!active)
          return;

        objectUrl = createObjectUrl(blob);
        image.decoding = "async";
        image.onload = () => {
          if (!active)
            return;

          active = false;
          clearImageHandlers();
          releaseObjectUrl();
          onComplete({ coverage });
        };
        image.onerror = () => {
          if (!active)
            return;

          active = false;
          clearImageHandlers();
          releaseObjectUrl();
          onError(new Error("The imagery API returned an unreadable PNG tile."));
        };
        image.src = objectUrl;
      } catch (error) {
        if (!active)
          return;

        active = false;
        clearImageHandlers();
        releaseObjectUrl();
        onError(error);
      }
    })();

    return () => {
      if (!active)
        return;

      active = false;
      controller.abort();
      clearImageHandlers();
      image.removeAttribute?.("src");
      releaseObjectUrl();
    };
  }

  function createGibsWarningReporter(onWarning) {
    let reportedUnresolvedCoverage = false;

    return result => {
      if (result.coverage === "complete" || reportedUnresolvedCoverage)
        return;

      reportedUnresolvedCoverage = true;
      onWarning(
        "Some areas have no current corrected-reflectance coverage from Terra, Aqua, "
        + "or VIIRS; unresolved pixels use the neutral map background.");
    };
  }

  function createGoogleMapsLoader(options = {}) {
    const windowObject = options.windowObject ?? globalThis.window;
    const documentObject = options.documentObject ?? globalThis.document;
    const setTimeoutFunction = options.setTimeoutFunction ?? globalThis.setTimeout;
    const clearTimeoutFunction = options.clearTimeoutFunction ?? globalThis.clearTimeout;
    const timeoutMilliseconds = options.timeoutMilliseconds ?? 15000;
    const callbackName = options.callbackName ?? "__thermalWatchGoogleMapsReady";
    const scriptId = options.scriptId ?? "thermalwatch-google-maps";
    const authenticationFailureHandlers = new Set();
    let loadPromise = null;
    let authenticationFailed = false;
    let authenticationFailureHookInstalled = false;
    let rejectPendingAuthentication = null;

    function authenticationError() {
      return new Error("Google Maps rejected the configured API key.");
    }

    function installAuthenticationFailureHook() {
      if (authenticationFailureHookInstalled)
        return;

      authenticationFailureHookInstalled = true;
      const previousHandler = windowObject.gm_authFailure;
      windowObject.gm_authFailure = () => {
        authenticationFailed = true;
        let previousError = null;
        try {
          if (typeof previousHandler === "function")
            previousHandler();
        } catch (error) {
          previousError = error;
        }

        rejectPendingAuthentication?.(authenticationError());
        authenticationFailureHandlers.forEach(handler => handler());
        if (previousError)
          throw previousError;
      };
    }

    function subscribeToAuthenticationFailure(handler) {
      installAuthenticationFailureHook();
      authenticationFailureHandlers.add(handler);
      return () => authenticationFailureHandlers.delete(handler);
    }

    function load(apiKey) {
      if (authenticationFailed)
        return Promise.reject(authenticationError());
      if (windowObject.google?.maps)
        return Promise.resolve();
      if (loadPromise)
        return loadPromise;
      if (typeof apiKey !== "string" || apiKey.length === 0)
        return Promise.reject(new Error("GOOGLE_MAPS_API_KEY is not configured."));
      if (!documentObject?.head || typeof documentObject.createElement !== "function") {
        return Promise.reject(
          new Error("The document cannot load the Google Maps JavaScript API."));
      }

      installAuthenticationFailureHook();
      let script = null;
      let timeoutId = null;
      let settled = false;
      let rejectForAuthentication = null;

      const attempt = new Promise((resolve, reject) => {
        const cleanup = failed => {
          if (timeoutId !== null)
            clearTimeoutFunction(timeoutId);
          timeoutId = null;
          if (windowObject[callbackName] === ready)
            delete windowObject[callbackName];
          if (rejectPendingAuthentication === rejectForAuthentication)
            rejectPendingAuthentication = null;
          if (failed)
            script?.remove();
        };

        const finish = error => {
          if (settled)
            return;

          settled = true;
          cleanup(Boolean(error));
          if (error)
            reject(error);
          else
            resolve();
        };

        const ready = () => {
          if (windowObject.google?.maps)
            finish(null);
          else
            finish(new Error("Google Maps loaded without exposing its map API."));
        };

        rejectForAuthentication = error => finish(error);
        rejectPendingAuthentication = rejectForAuthentication;
        windowObject[callbackName] = ready;

        script = documentObject.createElement("script");
        script.id = scriptId;
        script.async = true;
        script.referrerPolicy = "strict-origin-when-cross-origin";
        script.src = "https://maps.googleapis.com/maps/api/js"
          + `?key=${encodeURIComponent(apiKey)}`
          + `&v=weekly&loading=async&callback=${encodeURIComponent(callbackName)}`;
        script.addEventListener("error", () => finish(
          new Error("The Google Maps JavaScript API could not be downloaded.")), { once: true });

        timeoutId = setTimeoutFunction(() => finish(
          new Error("The Google Maps JavaScript API did not load within 15 seconds.")),
        timeoutMilliseconds);
        documentObject.head.append(script);
      });

      loadPromise = attempt.catch(error => {
        loadPromise = null;
        throw error;
      });
      return loadPromise;
    }

    return Object.freeze({
      load,
      subscribeToAuthenticationFailure
    });
  }

  return Object.freeze({
    imageryCoverageHeader,
    gibsTileApiUrl,
    yandexMapsUrl,
    validateNearbyFeatures,
    parseCoordinateInput,
    nearestCoordinatePoint,
    notificationMarkerStyle,
    coordinateSearchMarkerStyle,
    clusterPointKeys,
    createMapResizeScheduler,
    loadGibsTile,
    createGibsWarningReporter,
    createGoogleMapsLoader
  });
});
