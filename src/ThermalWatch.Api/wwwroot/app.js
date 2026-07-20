(() => {
  "use strict";

  const elements = {
    providerSelect: document.querySelector("#provider-select"),
    googleOption: document.querySelector('#provider-select option[value="google"]'),
    refreshButton: document.querySelector("#refresh-button"),
    notice: document.querySelector("#notice"),
    countSummary: document.querySelector("#count-summary"),
    healthSummary: document.querySelector("#health-summary"),
    generatedSummary: document.querySelector("#generated-summary"),
    map: document.querySelector("#map"),
    mapLoading: document.querySelector("#map-loading"),
    mapLoadingText: document.querySelector("#map-loading-text"),
    providerCaption: document.querySelector("#provider-caption"),
    detailsPanel: document.querySelector("#details-panel")
  };

  const state = {
    config: null,
    snapshot: null,
    points: [],
    malformedCount: 0,
    selectedKey: null,
    providerName: "gibs",
    provider: null,
    notices: new Map(),
    loadVersion: 0,
    mapVersion: 0
  };

  const providerDefinitions = new Map([
    ["gibs", {
      create: () => new GibsMapProvider(),
      caption: date => `NASA GIBS VIIRS true-color · ${date} UTC · Blue Marble fallback`
    }],
    ["google", {
      create: () => new GoogleMapProvider(),
      caption: () => "Google Maps Satellite"
    }]
  ]);

  const fieldLabels = {
    id: "Anomaly ID",
    countryCode: "Country",
    source: "FIRMS source",
    satellite: "Satellite",
    instrument: "Instrument",
    latitude: "Latitude",
    longitude: "Longitude",
    acquiredAtUtc: "Acquired at (UTC)",
    dayNight: "Day / night",
    brightnessKelvin: "Brightness (K)",
    secondaryBrightnessKelvin: "Secondary brightness (K)",
    thermalContrastKelvin: "Thermal contrast (K)",
    frpMegawatts: "FRP (MW)",
    scanKilometers: "Scan (km)",
    trackKilometers: "Track (km)",
    confidenceRaw: "Raw confidence",
    confidencePercent: "Confidence (%)",
    confidenceCategory: "Confidence category",
    version: "Product version",
    googleMapsUrl: "Google Maps URL",
    generatedAtUtc: "Snapshot generated (UTC)",
    activeWindowHours: "Active window (hours)",
    isReady: "Snapshot ready",
    isPartiallyStale: "Partially stale",
    configuredCountries: "Configured countries",
    count: "API anomaly count",
    country: "Status country",
    lastAttemptUtc: "Last attempt (UTC)",
    lastSuccessUtc: "Last success (UTC)",
    stale: "Source stale",
    error: "Source error",
    ingestionMode: "Ingestion mode"
  };

  elements.providerSelect.addEventListener("change", () => {
    void activateProvider(elements.providerSelect.value);
  });
  elements.refreshButton.addEventListener("click", () => {
    void loadViewer();
  });

  void loadViewer();

  async function loadViewer() {
    const version = ++state.loadVersion;
    const hadSnapshot = state.snapshot !== null;
    setBusy(true, hadSnapshot ? "Refreshing anomalies…" : "Loading anomalies…");
    setNotice("api", "info", hadSnapshot ? "Refreshing anomaly data…" : "Loading anomaly data…");

    const [configResult, anomalyResult] = await Promise.allSettled([
      fetchJson("/api/viewer/config"),
      fetchJson("/api/anomalies")
    ]);

    if (version !== state.loadVersion)
      return;

    if (configResult.status === "fulfilled") {
      configureGoogle(configResult.value);
      clearNotice("config");
    } else {
      configureGoogle(null);
      setNotice(
        "config",
        "warning",
        `Viewer configuration could not be loaded. NASA GIBS remains available. ${errorMessage(configResult.reason)}`);
    }

    if (anomalyResult.status === "rejected") {
      clearNotice("api");
      setNotice("api-error", "error", `Anomaly data could not be loaded. ${errorMessage(anomalyResult.reason)}`);
      setBusy(false);

      if (!hadSnapshot) {
        state.snapshot = null;
        state.points = [];
        state.malformedCount = 0;
        renderUnavailableSummary();
        renderEmptyDetails(
          "Anomalies unavailable",
          "The API request failed. Use Refresh data to try again.");
        await activateProvider(state.providerName, true);
      }
      return;
    }

    try {
      applySnapshot(anomalyResult.value);
    } catch (error) {
      clearNotice("api");
      setNotice("api-error", "error", `The anomaly API returned an invalid response. ${errorMessage(error)}`);
      setBusy(false);
      if (!hadSnapshot) {
        state.snapshot = null;
        state.points = [];
        renderUnavailableSummary();
        renderEmptyDetails("Invalid anomaly response", "The API response could not be displayed.");
        await activateProvider(state.providerName, true);
      }
      return;
    }

    clearNotice("api");
    clearNotice("api-error");
    updateSnapshotNotices();
    renderSnapshotSummary();

    if (state.selectedKey && !state.points.some(point => point.key === state.selectedKey))
      state.selectedKey = null;

    if (state.selectedKey) {
      renderDetails(state.points.find(point => point.key === state.selectedKey));
    } else if (state.points.length === 0) {
      renderEmptyDetails(
        state.snapshot.items.length === 0 ? "No current anomalies" : "No mappable anomalies",
        state.snapshot.items.length === 0
          ? "The current API snapshot contains no heat anomalies."
          : "Every returned observation has malformed coordinates and was omitted from the map.");
    } else {
      renderEmptyDetails(
        "Select an anomaly",
        "Choose any marker to inspect its complete FIRMS record and source diagnostics.");
    }

    await activateProvider(state.providerName, true);
    setBusy(false);
  }

  async function fetchJson(url) {
    const response = await fetch(url, {
      cache: "no-store",
      headers: { Accept: "application/json" }
    });

    let body;
    try {
      body = await response.json();
    } catch {
      throw new Error(`${url} returned a non-JSON response (HTTP ${response.status}).`);
    }

    if (!response.ok) {
      const detail = typeof body?.error === "string" ? ` ${body.error}` : "";
      throw new Error(`${url} returned HTTP ${response.status}.${detail}`);
    }

    return body;
  }

  function configureGoogle(config) {
    const key = typeof config?.googleMaps?.apiKey === "string"
      ? config.googleMaps.apiKey.trim()
      : "";
    const available = config?.googleMaps?.available === true && key.length > 0;

    state.config = { googleApiKey: available ? key : null };
    elements.googleOption.disabled = !available;
    elements.googleOption.textContent = available
      ? "Google Satellite"
      : "Google Satellite (key unavailable)";

    if (available) {
      clearNotice("google-key");
    } else {
      setNotice(
        "google-key",
        "info",
        "Google Satellite is unavailable because GOOGLE_MAPS_API_KEY is not configured.");
      if (state.providerName === "google") {
        state.providerName = "gibs";
        elements.providerSelect.value = "gibs";
      }
    }
  }

  function applySnapshot(snapshot) {
    if (!snapshot || typeof snapshot !== "object" || !Array.isArray(snapshot.items))
      throw new Error("Expected an object with an items array.");

    const occurrences = new Map();
    const points = [];
    let malformedCount = 0;

    snapshot.items.forEach((anomaly, index) => {
      if (!anomaly || typeof anomaly !== "object"
          || typeof anomaly.latitude !== "number"
          || typeof anomaly.longitude !== "number"
          || !Number.isFinite(anomaly.latitude)
          || !Number.isFinite(anomaly.longitude)
          || anomaly.latitude < -90
          || anomaly.latitude > 90
          || anomaly.longitude < -180
          || anomaly.longitude > 180) {
        malformedCount += 1;
        return;
      }

      const baseKey = typeof anomaly.id === "string" && anomaly.id.length > 0
        ? anomaly.id
        : `observation-${index + 1}`;
      const occurrence = (occurrences.get(baseKey) ?? 0) + 1;
      occurrences.set(baseKey, occurrence);
      points.push({
        key: occurrence === 1 ? baseKey : `${baseKey}#${occurrence}`,
        anomaly,
        latitude: anomaly.latitude,
        longitude: anomaly.longitude
      });
    });

    state.snapshot = snapshot;
    state.points = points;
    state.malformedCount = malformedCount;
  }

  function updateSnapshotNotices() {
    clearNotice("coordinates");
    clearNotice("snapshot");

    if (state.malformedCount > 0) {
      setNotice(
        "coordinates",
        "warning",
        `${state.malformedCount} ${plural(state.malformedCount, "observation has", "observations have")} malformed coordinates and ${plural(state.malformedCount, "was", "were")} omitted.`);
    }

    if (state.snapshot?.isPartiallyStale === true) {
      setNotice(
        "snapshot",
        "warning",
        "Some FIRMS sources are stale. Markers use the most recent complete data retained by the API.");
    } else if (state.snapshot?.isReady === false) {
      setNotice(
        "snapshot",
        "info",
        "The initial FIRMS snapshot is still warming up; no source has completed successfully yet.");
    }
  }

  async function activateProvider(name, force = false) {
    if (!providerDefinitions.has(name))
      name = "gibs";

    if (name === "google" && !state.config?.googleApiKey) {
      name = "gibs";
      setNotice("provider", "error", "Google Satellite requires GOOGLE_MAPS_API_KEY.");
    }

    if (!force && name === state.providerName && state.provider)
      return;

    state.providerName = name;
    elements.providerSelect.value = name;
    const version = ++state.mapVersion;
    clearNotice("provider");
    clearNotice("tiles");
    setMapLoading(true, `Loading ${name === "google" ? "Google Satellite" : "NASA GIBS"}…`);

    if (state.provider) {
      state.provider.destroy();
      state.provider = null;
    }
    elements.map.replaceChildren();

    const definition = providerDefinitions.get(name);
    const provider = definition.create();
    const imageryDate = newestAcquisitionDate(state.points);
    const context = {
      imageryDate,
      googleApiKey: state.config?.googleApiKey,
      onSelect: selectPoint,
      onWarning: message => {
        if (version === state.mapVersion)
          setNotice("tiles", "warning", message);
      },
      onError: message => {
        if (version === state.mapVersion)
          setNotice("provider", "error", message);
      }
    };

    try {
      await provider.mount(elements.map, context);
      if (version !== state.mapVersion) {
        provider.destroy();
        return;
      }

      provider.renderAnomalies(state.points);
      provider.setSelected(state.selectedKey);
      provider.fitToAnomalies(state.points);
      state.provider = provider;
      elements.providerCaption.textContent = definition.caption(imageryDate);
      setMapLoading(false);
    } catch (error) {
      provider.destroy();
      if (version !== state.mapVersion)
        return;

      state.provider = null;
      const message = errorMessage(error);
      setNotice(
        "provider",
        "error",
        `${name === "google" ? "Google Satellite" : "NASA GIBS"} could not be loaded. ${message}`);
      renderMapError(message);
      elements.providerCaption.textContent = "";
      setMapLoading(false);
    }
  }

  function selectPoint(key) {
    const point = state.points.find(candidate => candidate.key === key);
    if (!point)
      return;

    state.selectedKey = key;
    state.provider?.setSelected(key);
    renderDetails(point);
  }

  function renderSnapshotSummary() {
    const returnedCount = state.snapshot.items.length;
    elements.countSummary.textContent = state.malformedCount > 0
      ? `${state.points.length} mapped · ${returnedCount} returned`
      : `${returnedCount} ${plural(returnedCount, "anomaly", "anomalies")}`;

    if (state.snapshot.isPartiallyStale === true) {
      elements.healthSummary.className = "health-stale";
      elements.healthSummary.textContent = "Partial upstream data";
    } else if (state.snapshot.isReady === true) {
      elements.healthSummary.className = "health-ready";
      elements.healthSummary.textContent = "Snapshot ready";
    } else {
      elements.healthSummary.className = "";
      elements.healthSummary.textContent = "Snapshot warming up";
    }

    elements.generatedSummary.textContent = state.snapshot.generatedAtUtc
      ? `Generated ${formatDateTime(state.snapshot.generatedAtUtc)}`
      : "Generation time unavailable";
  }

  function renderUnavailableSummary() {
    elements.countSummary.textContent = "Anomalies unavailable";
    elements.healthSummary.className = "";
    elements.healthSummary.textContent = "";
    elements.generatedSummary.textContent = "";
  }

  function renderEmptyDetails(title, message) {
    const wrapper = document.createElement("div");
    wrapper.className = "details-empty";
    wrapper.append(
      textElement("p", "Anomaly details", "eyebrow"),
      textElement("h2", title),
      textElement("p", message)
    );
    elements.detailsPanel.replaceChildren(wrapper);
  }

  function renderDetails(point) {
    if (!point)
      return;

    const anomaly = point.anomaly;
    const wrapper = document.createElement("div");
    wrapper.className = "details-content";
    wrapper.append(
      textElement("p", "Selected anomaly", "eyebrow"),
      textElement("h2", anomaly.countryCode || "Heat anomaly"),
      textElement(
        "p",
        [anomaly.source, anomaly.satellite, formatDateTime(anomaly.acquiredAtUtc)]
          .filter(Boolean)
          .join(" · "),
        "details-subtitle")
    );

    const observationSection = section("Observation data");
    observationSection.append(fieldList(Object.entries(anomaly)));
    wrapper.append(observationSection);

    const debugSection = section("Snapshot debug information");
    debugSection.append(fieldList([
      ["generatedAtUtc", state.snapshot?.generatedAtUtc],
      ["activeWindowHours", state.snapshot?.activeWindowHours],
      ["isReady", state.snapshot?.isReady],
      ["isPartiallyStale", state.snapshot?.isPartiallyStale],
      ["configuredCountries", state.snapshot?.configuredCountries],
      ["count", state.snapshot?.count]
    ]));

    const sourceStatus = Array.isArray(state.snapshot?.sources)
      ? state.snapshot.sources.find(status =>
          status?.country === anomaly.countryCode && status?.source === anomaly.source)
      : null;

    debugSection.append(textElement("h3", "Matching source status"));
    debugSection.append(sourceStatus
      ? fieldList(Object.entries(sourceStatus))
      : textElement("p", "No matching source status was returned.", "details-subtitle"));
    wrapper.append(debugSection);

    const rawSection = section("Raw record");
    const disclosure = document.createElement("details");
    disclosure.className = "raw-record";
    disclosure.append(textElement("summary", "Show anomaly JSON"));
    const pre = document.createElement("pre");
    pre.textContent = JSON.stringify(anomaly, null, 2);
    disclosure.append(pre);
    rawSection.append(disclosure);
    wrapper.append(rawSection);

    elements.detailsPanel.replaceChildren(wrapper);
    elements.detailsPanel.scrollTop = 0;
  }

  function section(title) {
    const element = document.createElement("section");
    element.className = "details-section";
    element.append(textElement("h3", title));
    return element;
  }

  function fieldList(entries) {
    const list = document.createElement("dl");
    list.className = "field-list";

    entries.forEach(([key, value]) => {
      list.append(textElement("dt", fieldLabels[key] ?? humanize(key)));
      const definition = document.createElement("dd");
      definition.append(formatFieldValue(key, value));
      list.append(definition);
    });
    return list;
  }

  function formatFieldValue(key, value) {
    if (value === null || value === undefined || value === "")
      return document.createTextNode("Not available");

    if (key === "googleMapsUrl" && typeof value === "string") {
      try {
        const url = new URL(value);
        if (url.protocol === "https:" || url.protocol === "http:") {
          const link = document.createElement("a");
          link.href = url.href;
          link.target = "_blank";
          link.rel = "noopener noreferrer";
          link.textContent = "Open in Google Maps ↗";
          return link;
        }
      } catch {
        // The raw value is shown below when the URL is malformed.
      }
    }

    if (typeof value === "boolean")
      return document.createTextNode(value ? "Yes" : "No");

    if (Array.isArray(value))
      return document.createTextNode(value.length > 0 ? value.join(", ") : "None");

    if (typeof value === "number") {
      const digits = key === "latitude" || key === "longitude" ? 6 : 3;
      return document.createTextNode(new Intl.NumberFormat(undefined, {
        maximumFractionDigits: digits
      }).format(value));
    }

    if (key.endsWith("Utc") && typeof value === "string")
      return document.createTextNode(`${formatDateTime(value)} (${value})`);

    if (typeof value === "object")
      return document.createTextNode(JSON.stringify(value));

    return document.createTextNode(String(value));
  }

  function humanize(value) {
    return String(value)
      .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
      .replace(/^./, character => character.toUpperCase());
  }

  function textElement(tag, text, className) {
    const element = document.createElement(tag);
    if (className)
      element.className = className;
    element.textContent = text ?? "";
    return element;
  }

  function setNotice(key, kind, message) {
    state.notices.set(key, { kind, message });
    renderNotices();
  }

  function clearNotice(key) {
    if (state.notices.delete(key))
      renderNotices();
  }

  function renderNotices() {
    if (state.notices.size === 0) {
      elements.notice.hidden = true;
      elements.notice.replaceChildren();
      return;
    }

    const values = [...state.notices.values()];
    const priority = { info: 0, warning: 1, error: 2 };
    const kind = values.reduce(
      (current, notice) => priority[notice.kind] > priority[current] ? notice.kind : current,
      "info");
    const list = document.createElement("ul");
    values.forEach(notice => list.append(textElement("li", notice.message)));
    elements.notice.dataset.kind = kind;
    elements.notice.replaceChildren(list);
    elements.notice.hidden = false;
  }

  function setBusy(busy, message) {
    elements.refreshButton.disabled = busy;
    elements.refreshButton.textContent = busy ? "Loading…" : "Refresh data";
    elements.providerSelect.disabled = busy;
    if (busy)
      setMapLoading(true, message);
    else
      setMapLoading(false);
  }

  function setMapLoading(loading, message = "Loading map…") {
    elements.mapLoadingText.textContent = message;
    elements.mapLoading.hidden = !loading;
  }

  function renderMapError(message) {
    const wrapper = document.createElement("div");
    wrapper.className = "map-error";
    wrapper.append(
      textElement("h2", "Map unavailable"),
      textElement("p", message)
    );
    elements.map.replaceChildren(wrapper);
  }

  function newestAcquisitionDate(points) {
    const latest = points.reduce((current, point) => {
      const timestamp = Date.parse(point.anomaly.acquiredAtUtc);
      return Number.isFinite(timestamp) && timestamp > current ? timestamp : current;
    }, 0) || Date.now();
    return new Date(latest).toISOString().slice(0, 10);
  }

  function formatDateTime(value) {
    const date = new Date(value);
    if (!Number.isFinite(date.getTime()))
      return value ? String(value) : "";
    return new Intl.DateTimeFormat(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
      timeZone: "UTC",
      timeZoneName: "short"
    }).format(date);
  }

  function markerLabel(point) {
    const anomaly = point.anomaly;
    return [
      anomaly.countryCode || "Heat anomaly",
      anomaly.source,
      formatDateTime(anomaly.acquiredAtUtc)
    ].filter(Boolean).join(" · ");
  }

  function markerStyle(selected) {
    return selected
      ? { fill: "#ffd166", stroke: "#ffffff", size: 9, weight: 2 }
      : { fill: "#ff593d", stroke: "#ffffff", size: 7, weight: 2 };
  }

  function errorMessage(error) {
    if (error instanceof Error && error.message)
      return error.message;
    return "An unexpected error occurred.";
  }

  function plural(count, singular, pluralForm) {
    return count === 1 ? singular : pluralForm;
  }

  class GibsMapProvider {
    constructor() {
      this.map = null;
      this.markers = new Map();
      this.markerLayer = null;
      this.onSelect = null;
      this.datedLayer = null;
      this.reportedTileError = false;
    }

    async mount(container, context) {
      if (!window.L)
        throw new Error("The Leaflet map library could not be downloaded.");

      this.onSelect = context.onSelect;
      this.map = window.L.map(container, {
        minZoom: 1,
        maxZoom: 14,
        preferCanvas: true,
        worldCopyJump: true
      }).setView([20, 0], 2);

      const commonOptions = {
        attribution: '&copy; <a href="https://earthdata.nasa.gov/gibs">NASA GIBS</a>',
        bounds: [[-85.0511, -180], [85.0511, 180]],
        crossOrigin: true,
        noWrap: false
      };
      window.L.tileLayer(
        "https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/BlueMarble_NextGeneration/default/default/GoogleMapsCompatible_Level8/{z}/{y}/{x}.jpeg",
        {
          ...commonOptions,
          maxNativeZoom: 8,
          maxZoom: 14,
          zIndex: 1
        }).addTo(this.map);

      this.datedLayer = window.L.tileLayer(
        `https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/VIIRS_SNPP_CorrectedReflectance_TrueColor/default/${encodeURIComponent(context.imageryDate)}/GoogleMapsCompatible_Level9/{z}/{y}/{x}.jpg`,
        {
          ...commonOptions,
          maxNativeZoom: 9,
          maxZoom: 14,
          opacity: 1,
          zIndex: 2
        }).addTo(this.map);
      this.datedLayer.on("tileerror", () => {
        if (this.reportedTileError)
          return;
        this.reportedTileError = true;
        context.onWarning(
          "Some dated NASA GIBS tiles are unavailable; Blue Marble imagery is showing underneath.");
      });

      this.markerLayer = window.L.layerGroup().addTo(this.map);
    }

    renderAnomalies(points) {
      this.markerLayer.clearLayers();
      this.markers.clear();

      points.forEach(point => {
        const visual = markerStyle(false);
        const marker = window.L.circleMarker([point.latitude, point.longitude], {
          radius: visual.size,
          color: visual.stroke,
          weight: visual.weight,
          fillColor: visual.fill,
          fillOpacity: 0.88,
          opacity: 0.95
        });
        const tooltip = document.createElement("span");
        tooltip.textContent = markerLabel(point);
        marker.bindTooltip(tooltip, { direction: "top", offset: [0, -6] });
        marker.on("click", () => this.onSelect(point.key));
        marker.addTo(this.markerLayer);
        this.markers.set(point.key, marker);
      });
    }

    setSelected(key) {
      this.markers.forEach((marker, markerKey) => {
        const visual = markerStyle(markerKey === key);
        marker.setStyle({
          radius: visual.size,
          color: visual.stroke,
          weight: visual.weight,
          fillColor: visual.fill
        });
        if (markerKey === key)
          marker.bringToFront();
      });
    }

    fitToAnomalies(points) {
      if (points.length === 0) {
        this.map.setView([20, 0], 2);
      } else if (points.length === 1) {
        this.map.setView([points[0].latitude, points[0].longitude], 8);
      } else {
        const bounds = window.L.latLngBounds(
          points.map(point => [point.latitude, point.longitude]));
        this.map.fitBounds(bounds, { padding: [42, 42], maxZoom: 10 });
      }
    }

    destroy() {
      if (this.map)
        this.map.remove();
      this.map = null;
      this.markerLayer = null;
      this.datedLayer = null;
      this.markers.clear();
    }
  }

  class GoogleMapProvider {
    constructor() {
      this.map = null;
      this.markers = new Map();
      this.onSelect = null;
      this.unsubscribeAuthFailure = null;
      this.authFailure = null;
    }

    async mount(container, context) {
      if (!context.googleApiKey)
        throw new Error("GOOGLE_MAPS_API_KEY is not configured.");

      this.onSelect = context.onSelect;
      this.unsubscribeAuthFailure = subscribeToGoogleAuthFailure(() => {
        this.authFailure = new Error("Google Maps rejected the configured API key.");
        context.onError(
          "Google Maps rejected GOOGLE_MAPS_API_KEY. Check API enablement, billing, and referrer restrictions.");
      });
      await loadGoogleMaps(context.googleApiKey);
      if (this.authFailure)
        throw this.authFailure;

      this.map = new window.google.maps.Map(container, {
        center: { lat: 20, lng: 0 },
        zoom: 2,
        backgroundColor: "#111d21",
        clickableIcons: false,
        fullscreenControl: true,
        mapTypeControl: false,
        mapTypeId: window.google.maps.MapTypeId.SATELLITE,
        streetViewControl: false
      });
    }

    renderAnomalies(points) {
      this.clearMarkers();
      points.forEach(point => {
        const marker = new window.google.maps.Marker({
          map: this.map,
          position: { lat: point.latitude, lng: point.longitude },
          title: markerLabel(point),
          icon: googleMarkerIcon(false),
          optimized: true
        });
        marker.addListener("click", () => this.onSelect(point.key));
        this.markers.set(point.key, marker);
      });
    }

    setSelected(key) {
      this.markers.forEach((marker, markerKey) => {
        const selected = markerKey === key;
        marker.setIcon(googleMarkerIcon(selected));
        marker.setZIndex(selected ? 1000 : undefined);
      });
    }

    fitToAnomalies(points) {
      if (points.length === 0) {
        this.map.setCenter({ lat: 20, lng: 0 });
        this.map.setZoom(2);
      } else if (points.length === 1) {
        this.map.setCenter({ lat: points[0].latitude, lng: points[0].longitude });
        this.map.setZoom(8);
      } else {
        const bounds = new window.google.maps.LatLngBounds();
        points.forEach(point => bounds.extend({ lat: point.latitude, lng: point.longitude }));
        this.map.fitBounds(bounds, 42);
        window.google.maps.event.addListenerOnce(this.map, "idle", () => {
          if (this.map.getZoom() > 10)
            this.map.setZoom(10);
        });
      }
    }

    clearMarkers() {
      this.markers.forEach(marker => {
        window.google.maps.event.clearInstanceListeners(marker);
        marker.setMap(null);
      });
      this.markers.clear();
    }

    destroy() {
      if (window.google?.maps)
        this.clearMarkers();
      this.unsubscribeAuthFailure?.();
      this.unsubscribeAuthFailure = null;
      this.map = null;
    }
  }

  function googleMarkerIcon(selected) {
    const visual = markerStyle(selected);
    return {
      path: window.google.maps.SymbolPath.CIRCLE,
      fillColor: visual.fill,
      fillOpacity: 0.9,
      scale: visual.size,
      strokeColor: visual.stroke,
      strokeOpacity: 1,
      strokeWeight: visual.weight
    };
  }

  let googleMapsPromise = null;
  const googleAuthFailureHandlers = new Set();
  let googleAuthFailureHookInstalled = false;

  function subscribeToGoogleAuthFailure(handler) {
    installGoogleAuthFailureHook();
    googleAuthFailureHandlers.add(handler);
    return () => googleAuthFailureHandlers.delete(handler);
  }

  function installGoogleAuthFailureHook() {
    if (googleAuthFailureHookInstalled)
      return;

    googleAuthFailureHookInstalled = true;
    const previousHandler = window.gm_authFailure;
    window.gm_authFailure = () => {
      if (typeof previousHandler === "function")
        previousHandler();
      googleAuthFailureHandlers.forEach(handler => handler());
    };
  }

  function loadGoogleMaps(apiKey) {
    if (window.google?.maps)
      return Promise.resolve();
    if (googleMapsPromise)
      return googleMapsPromise;

    installGoogleAuthFailureHook();
    const callbackName = "__thermalWatchGoogleMapsReady";
    const attempt = new Promise((resolve, reject) => {
      window[callbackName] = () => {
        delete window[callbackName];
        if (window.google?.maps)
          resolve();
        else
          reject(new Error("Google Maps loaded without exposing its map API."));
      };

      const script = document.createElement("script");
      script.id = "thermalwatch-google-maps";
      script.async = true;
      script.referrerPolicy = "strict-origin-when-cross-origin";
      script.src = `https://maps.googleapis.com/maps/api/js?key=${encodeURIComponent(apiKey)}&v=weekly&loading=async&callback=${callbackName}`;
      script.addEventListener("error", () => {
        delete window[callbackName];
        script.remove();
        reject(new Error("The Google Maps JavaScript API could not be downloaded."));
      }, { once: true });
      document.head.append(script);
    });

    googleMapsPromise = attempt.catch(error => {
      googleMapsPromise = null;
      throw error;
    });
    return googleMapsPromise;
  }
})();
