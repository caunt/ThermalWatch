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
    url.searchParams.set("l", "map");
    return url.href;
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
    loadGibsTile,
    createGibsWarningReporter,
    createGoogleMapsLoader
  });
});
