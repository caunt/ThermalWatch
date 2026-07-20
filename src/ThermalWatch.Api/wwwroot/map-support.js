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

  const gibsTileSize = 256;
  const gibsNoDataMaximum = 12;

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
      + `GoogleMapsCompatible_Level9/${coordinates.z}/${coordinates.y}/${coordinates.x}.jpeg`;
  }

  function isGibsNoDataPixel(pixels, offset, maximum = gibsNoDataMaximum) {
    return pixels[offset + 3] === 0
      || (pixels[offset] <= maximum
        && pixels[offset + 1] <= maximum
        && pixels[offset + 2] <= maximum);
  }

  function mergeGibsPixels(destination, source, maximum = gibsNoDataMaximum) {
    if (!destination
        || !source
        || destination.length !== source.length
        || destination.length % 4 !== 0) {
      throw new Error("Equal RGBA pixel buffers are required for NASA GIBS compositing.");
    }

    let filledPixels = 0;
    for (let offset = 0; offset < destination.length; offset += 4) {
      if (destination[offset + 3] !== 0 || isGibsNoDataPixel(source, offset, maximum))
        continue;

      destination[offset] = source[offset];
      destination[offset + 1] = source[offset + 1];
      destination[offset + 2] = source[offset + 2];
      destination[offset + 3] = source[offset + 3];
      filledPixels += 1;
    }

    return filledPixels;
  }

  function readGibsImagePixels(image, canvas) {
    canvas.width = gibsTileSize;
    canvas.height = gibsTileSize;
    const context = canvas.getContext("2d", { willReadFrequently: true });
    if (!context)
      throw new Error("A 2D canvas context is required for NASA GIBS compositing.");

    context.clearRect(0, 0, gibsTileSize, gibsTileSize);
    context.drawImage(image, 0, 0, gibsTileSize, gibsTileSize);
    return context.getImageData(0, 0, gibsTileSize, gibsTileSize).data;
  }

  function writeGibsPixels(canvas, pixels) {
    const context = canvas.getContext("2d");
    if (!context)
      throw new Error("A 2D canvas context is required for NASA GIBS compositing.");

    const imageData = context.createImageData(gibsTileSize, gibsTileSize);
    imageData.data.set(pixels);
    context.putImageData(imageData, 0, 0);
  }

  function loadGibsTile(canvas, coordinates, options = {}) {
    if (!canvas)
      throw new Error("A canvas element is required to load a NASA GIBS tile.");

    const products = options.products ?? gibsProducts;
    const buildUrl = options.buildUrl ?? gibsTileUrl;
    const createImage = options.createImage ?? (() => new globalThis.Image());
    const createCanvas = options.createCanvas
      ?? (() => globalThis.document.createElement("canvas"));
    let scratchCanvas = null;
    const readPixels = options.readPixels ?? (image => {
      scratchCanvas ??= createCanvas();
      return readGibsImagePixels(image, scratchCanvas);
    });
    const writePixels = options.writePixels ?? writeGibsPixels;
    const onComplete = options.onComplete ?? (() => {});
    const totalPixels = gibsTileSize * gibsTileSize;
    const output = new Uint8ClampedArray(totalPixels * 4);
    const usedProducts = [];
    let remainingPixels = totalPixels;
    let productIndex = 0;
    let active = true;
    let image = null;

    canvas.width = gibsTileSize;
    canvas.height = gibsTileSize;

    const clearImageHandlers = () => {
      if (!image)
        return;

      image.onload = null;
      image.onerror = null;
    };

    const complete = () => {
      if (!active)
        return;

      active = false;
      clearImageHandlers();
      writePixels(canvas, output);
      onComplete({
        available: remainingPixels < totalPixels,
        complete: remainingPixels === 0,
        unresolvedPixels: remainingPixels,
        usedProducts: [...usedProducts]
      });
    };

    const attempt = () => {
      const product = products[productIndex];
      if (!product) {
        complete();
        return;
      }

      image = createImage();
      image.crossOrigin = "anonymous";
      image.decoding = "async";
      image.onload = () => {
        if (!active)
          return;

        clearImageHandlers();
        let source;
        try {
          source = readPixels(image);
        } catch {
          productIndex += 1;
          attempt();
          return;
        }

        const filledPixels = mergeGibsPixels(output, source);
        if (filledPixels > 0) {
          usedProducts.push({ product, productIndex, filledPixels });
          remainingPixels -= filledPixels;
        }

        if (remainingPixels === 0) {
          complete();
          return;
        }

        productIndex += 1;
        attempt();
      };
      image.onerror = () => {
        if (!active)
          return;

        clearImageHandlers();
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
      clearImageHandlers();
      image?.removeAttribute?.("src");
    };
  }

  function createGibsWarningReporter(onWarning) {
    let reportedUnresolvedCoverage = false;

    return result => {
      if (result.complete || reportedUnresolvedCoverage)
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
    gibsProducts,
    gibsTileSize,
    gibsNoDataMaximum,
    gibsTileUrl,
    isGibsNoDataPixel,
    mergeGibsPixels,
    loadGibsTile,
    createGibsWarningReporter,
    createGoogleMapsLoader
  });
});
