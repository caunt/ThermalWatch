(function (root, factory) {
  "use strict";

  const support = factory();
  if (typeof module === "object" && module.exports)
    module.exports = support;
  else if (root)
    root.ThermalWatchMapSupport = support;
})(typeof globalThis === "object" ? globalThis : this, () => {
  "use strict";

  const gibsProducts = Object.freeze([
    Object.freeze({
      id: "modis-terra",
      label: "MODIS Terra",
      layer: "MODIS_Terra_CorrectedReflectance_TrueColor"
    }),
    Object.freeze({
      id: "modis-aqua",
      label: "MODIS Aqua",
      layer: "MODIS_Aqua_CorrectedReflectance_TrueColor"
    }),
    Object.freeze({
      id: "viirs-noaa21",
      label: "VIIRS NOAA-21",
      layer: "VIIRS_NOAA21_CorrectedReflectance_TrueColor"
    }),
    Object.freeze({
      id: "viirs-noaa20",
      label: "VIIRS NOAA-20",
      layer: "VIIRS_NOAA20_CorrectedReflectance_TrueColor"
    }),
    Object.freeze({
      id: "viirs-snpp",
      label: "VIIRS Suomi-NPP",
      layer: "VIIRS_SNPP_CorrectedReflectance_TrueColor"
    })
  ]);

  const blankTileSource =
    "data:image/gif;base64,R0lGODlhAQABAAAAACwAAAAAAQABAAA=";

  function gibsTileUrl(product, coordinates) {
    if (!product?.layer)
      throw new Error("A NASA GIBS product layer is required.");
    if (!coordinates
        || !Number.isInteger(coordinates.x)
        || !Number.isInteger(coordinates.y)
        || !Number.isInteger(coordinates.z)) {
      throw new Error("Integer NASA GIBS tile coordinates are required.");
    }

    return "https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/"
      + `${encodeURIComponent(product.layer)}/default/default/`
      + `GoogleMapsCompatible_Level9/${coordinates.z}/${coordinates.y}/${coordinates.x}.jpg`;
  }

  function loadGibsTile(image, coordinates, options = {}) {
    if (!image)
      throw new Error("An image element is required to load a NASA GIBS tile.");

    const products = options.products ?? gibsProducts;
    const buildUrl = options.buildUrl ?? gibsTileUrl;
    const onComplete = options.onComplete ?? (() => {});
    let productIndex = 0;
    let active = true;

    const complete = result => {
      if (!active)
        return;

      active = false;
      image.onload = null;
      image.onerror = null;
      onComplete(result);
    };

    const attempt = () => {
      const product = products[productIndex];
      if (!product) {
        image.onload = null;
        image.onerror = null;
        image.src = blankTileSource;
        complete({ available: false, product: null, productIndex: -1 });
        return;
      }

      image.onload = () => complete({
        available: true,
        product,
        productIndex
      });
      image.onerror = () => {
        if (!active)
          return;
        productIndex += 1;
        attempt();
      };
      image.src = buildUrl(product, coordinates);
    };

    attempt();
    return () => {
      if (!active) {
        return;
      }

      active = false;
      image.onload = null;
      image.onerror = null;
    };
  }

  function createGibsWarningReporter(onWarning) {
    let reportedFallback = false;
    let reportedExhaustion = false;

    return result => {
      if (!result.available) {
        if (reportedExhaustion)
          return;

        reportedExhaustion = true;
        onWarning(
          "Some latest NASA GIBS tiles are unavailable from every configured satellite; "
          + "those areas are left blank rather than showing historical imagery.");
        return;
      }

      if (result.productIndex > 0 && !reportedFallback && !reportedExhaustion) {
        reportedFallback = true;
        onWarning(
          "Some MODIS Terra tiles are unavailable; latest Aqua or VIIRS "
          + "corrected-reflectance imagery is shown where available.");
      }
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
    gibsProducts,
    gibsTileUrl,
    loadGibsTile,
    createGibsWarningReporter,
    createGoogleMapsLoader
  });
});
