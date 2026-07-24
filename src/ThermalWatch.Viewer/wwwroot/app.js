(() => {
  "use strict";

  const mapSupport = window.ThermalWatchMapSupport;
  if (!mapSupport)
    throw new Error("The ThermalWatch map support module could not be loaded.");

  const coordinateSearchZoom = 7;
  const googleMapsLoader = mapSupport.createGoogleMapsLoader({
    windowObject: window,
    documentObject: document
  });

  const elements = {
    coordinateSearchForm: document.querySelector("#coordinate-search"),
    coordinateSearchInput: document.querySelector("#coordinate-search-input"),
    coordinateSearchButton: document.querySelector("#coordinate-search-button"),
    coordinateSearchFeedback: document.querySelector("#coordinate-search-feedback"),
    providerSelect: document.querySelector("#provider-select"),
    googleOption: document.querySelector('#provider-select option[value="google"]'),
    refreshButton: document.querySelector("#refresh-button"),
    refreshButtonLabel: document.querySelector("#refresh-button-label"),
    notice: document.querySelector("#notice"),
    countSummary: document.querySelector("#count-summary"),
    healthSummary: document.querySelector("#health-summary"),
    generatedSummary: document.querySelector("#generated-summary"),
    map: document.querySelector("#map"),
    mapLoading: document.querySelector("#map-loading"),
    mapLoadingText: document.querySelector("#map-loading-text"),
    providerCaption: document.querySelector("#provider-caption"),
    eligibleClustersCount: document.querySelector("#eligible-clusters-count"),
    eligibleClustersContent: document.querySelector("#eligible-clusters-content"),
    detailsPanel: document.querySelector("#details-panel")
  };

  const state = {
    config: null,
    snapshot: null,
    points: [],
    malformedCount: 0,
    selectedKey: null,
    diagnostic: null,
    diagnosticStatus: "idle",
    diagnosticError: null,
    diagnosticVersion: 0,
    diagnosticAbortController: null,
    clusterKeys: new Set(),
    eligibleClusters: [],
    eligibleClustersStatus: "loading",
    eligibleClustersError: null,
    eligibleClustersSnapshotGeneratedAtUtc: null,
    eligibleClustersEvaluatedCount: 0,
    eligibleClustersVersion: 0,
    eligibleClustersAbortController: null,
    searchCoordinate: null,
    providerName: "gibs",
    provider: null,
    notices: new Map(),
    loadVersion: 0,
    mapVersion: 0
  };

  const providerDefinitions = new Map([
    ["gibs", {
      create: () => new GibsMapProvider(),
      caption: () => "NASA GIBS latest corrected reflectance · composed and served by ThermalWatch"
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
    googleMapsUrl: "Map links",
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
  elements.coordinateSearchForm.addEventListener("submit", event => {
    event.preventDefault();
    searchCoordinates();
  });
  elements.coordinateSearchInput.addEventListener("input", () => {
    if (elements.coordinateSearchInput.getAttribute("aria-invalid") === "true")
      setCoordinateSearchFeedback(null, "");
  });
  elements.refreshButton.addEventListener("click", () => {
    void loadViewer();
  });
  window.addEventListener("resize", mapSupport.createMapResizeScheduler(
    () => state.provider?.resize(),
    { requestAnimationFrameFunction: callback => window.requestAnimationFrame(callback) }));

  restoreCoordinateSearch();
  void loadViewer();

  async function loadViewer() {
    const version = ++state.loadVersion;
    const hadSnapshot = state.snapshot !== null;
    void loadEligibleNotificationClusters();
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

    if (state.searchCoordinate) {
      selectNearestSearchResult();
    } else if (state.points.length === 0) {
      clearSelection();
      renderNoAnomalies();
    } else {
      if (state.selectedKey && !state.points.some(point => point.key === state.selectedKey)) {
        resetDiagnostic();
        state.selectedKey = null;
      }

      if (state.selectedKey) {
        const selectedPoint = state.points.find(point => point.key === state.selectedKey);
        resetDiagnostic();
        state.diagnosticStatus = "loading";
        renderDetails(selectedPoint);
        void loadNotificationDiagnostic(selectedPoint);
      } else {
        renderEmptyDetails(
          "Select an anomaly",
          "Choose any marker to inspect its complete FIRMS record and source diagnostics.");
      }
    }

    await activateProvider(state.providerName, true);
    setBusy(false);
  }

  async function fetchJson(url, signal = undefined) {
    const response = await fetch(url, {
      cache: "no-store",
      headers: { Accept: "application/json" },
      signal
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

  async function loadEligibleNotificationClusters() {
    state.eligibleClustersAbortController?.abort();
    const controller = new AbortController();
    const version = ++state.eligibleClustersVersion;
    state.eligibleClustersAbortController = controller;
    state.eligibleClusters = [];
    state.eligibleClustersStatus = "loading";
    state.eligibleClustersError = null;
    state.eligibleClustersSnapshotGeneratedAtUtc = null;
    state.eligibleClustersEvaluatedCount = 0;
    renderEligibleNotificationClusters();

    try {
      const result = await fetchJson(
        "/api/viewer/eligible-notification-clusters",
        controller.signal);
      if (version !== state.eligibleClustersVersion)
        return;

      validateEligibleNotificationClusters(result);
      state.eligibleClusters = result.clusters;
      state.eligibleClustersStatus = "ready";
      state.eligibleClustersError = null;
      state.eligibleClustersSnapshotGeneratedAtUtc = result.snapshotGeneratedAtUtc;
      state.eligibleClustersEvaluatedCount = result.evaluatedClusterCount;
      renderEligibleNotificationClusters();
    } catch (error) {
      if (error?.name === "AbortError" || version !== state.eligibleClustersVersion)
        return;

      state.eligibleClusters = [];
      state.eligibleClustersStatus = "error";
      state.eligibleClustersError = errorMessage(error);
      state.eligibleClustersSnapshotGeneratedAtUtc = null;
      state.eligibleClustersEvaluatedCount = 0;
      renderEligibleNotificationClusters();
    } finally {
      if (version === state.eligibleClustersVersion)
        state.eligibleClustersAbortController = null;
    }
  }

  function validateEligibleNotificationClusters(result) {
    if (!result || typeof result !== "object"
        || typeof result.snapshotGeneratedAtUtc !== "string"
        || !Number.isFinite(new Date(result.snapshotGeneratedAtUtc).getTime())
        || !Number.isSafeInteger(result.evaluatedClusterCount)
        || result.evaluatedClusterCount < 0
        || !Number.isSafeInteger(result.eligibleClusterCount)
        || result.eligibleClusterCount < 0
        || !Array.isArray(result.clusters)
        || result.eligibleClusterCount !== result.clusters.length
        || result.evaluatedClusterCount < result.eligibleClusterCount) {
      throw new Error("The eligible notification cluster response is invalid.");
    }

    const clusterIds = new Set();
    result.clusters.forEach(cluster => {
      if (!cluster || typeof cluster !== "object"
          || typeof cluster.clusterId !== "string"
          || cluster.clusterId.trim().length === 0
          || typeof cluster.representativeId !== "string"
          || cluster.representativeId.trim().length === 0
          || typeof cluster.countryCode !== "string"
          || cluster.countryCode.trim().length === 0
          || typeof cluster.source !== "string"
          || cluster.source.trim().length === 0
          || typeof cluster.satellite !== "string"
          || cluster.satellite.trim().length === 0
          || typeof cluster.acquiredAtUtc !== "string"
          || !Number.isFinite(new Date(cluster.acquiredAtUtc).getTime())
          || cluster.frpMegawatts !== null && !Number.isFinite(cluster.frpMegawatts)
          || !Number.isSafeInteger(cluster.detectionCount)
          || cluster.detectionCount <= 0
          || !Number.isFinite(cluster.clusterDiameterKilometers)
          || cluster.clusterDiameterKilometers < 0
          || clusterIds.has(cluster.clusterId)) {
        throw new Error("An eligible notification cluster is invalid.");
      }

      mapSupport.eligibleNotificationClusterCoordinate(cluster);
      clusterIds.add(cluster.clusterId);
    });
  }

  function renderEligibleNotificationClusters() {
    if (state.eligibleClustersStatus === "loading") {
      elements.eligibleClustersCount.textContent = "Checking…";
      const loading = document.createElement("div");
      loading.className = "eligible-clusters-state";
      loading.append(
        textElement("span", "", "spinner"),
        textElement("p", "Evaluating every active cluster against the enabled criteria…"));
      elements.eligibleClustersContent.replaceChildren(loading);
      return;
    }

    if (state.eligibleClustersStatus === "error") {
      elements.eligibleClustersCount.textContent = "Unavailable";
      const error = document.createElement("div");
      error.className = "eligible-clusters-state error";
      error.append(
        textElement("strong", "Eligible clusters unavailable"),
        textElement(
          "p",
          `${state.eligibleClustersError || "The evaluation failed."} Use Refresh data to try again.`));
      elements.eligibleClustersContent.replaceChildren(error);
      return;
    }

    const count = state.eligibleClusters.length;
    elements.eligibleClustersCount.textContent = `${count} eligible`;
    const wrapper = document.createElement("div");
    wrapper.className = "eligible-clusters-ready";
    wrapper.append(
      textElement(
        "p",
        `${count} of ${state.eligibleClustersEvaluatedCount} ${plural(state.eligibleClustersEvaluatedCount, "active cluster", "active clusters")} pass all enabled content criteria.`,
        "eligible-clusters-summary"),
      textElement(
        "p",
        "Startup and previously delivered episode suppression are not applied.",
        "eligible-clusters-policy"));

    if (count === 0) {
      wrapper.append(textElement(
        "p",
        state.eligibleClustersEvaluatedCount === 0
          ? "No active notification clusters are available in this snapshot."
          : "No active cluster currently passes every enabled criterion.",
        "eligible-clusters-empty"));
      elements.eligibleClustersContent.replaceChildren(wrapper);
      return;
    }

    const list = document.createElement("ol");
    list.className = "eligible-clusters-list";
    state.eligibleClusters.forEach(cluster => list.append(eligibleNotificationClusterItem(cluster)));
    wrapper.append(list);
    if (state.eligibleClustersSnapshotGeneratedAtUtc) {
      wrapper.append(textElement(
        "p",
        `Evaluated snapshot ${formatDateTime(state.eligibleClustersSnapshotGeneratedAtUtc)}.`,
        "eligible-clusters-timestamp"));
    }
    elements.eligibleClustersContent.replaceChildren(wrapper);
  }

  function eligibleNotificationClusterItem(cluster) {
    const coordinate = mapSupport.eligibleNotificationClusterCoordinate(cluster);
    const item = document.createElement("li");
    const button = document.createElement("button");
    button.className = "eligible-cluster-button";
    button.type = "button";
    button.setAttribute(
      "aria-label",
      `Search the representative coordinates for the ${cluster.countryCode} cluster at ${formatSearchCoordinate(coordinate)}`);
    button.addEventListener("click", () => searchEligibleNotificationCluster(cluster));

    const heading = document.createElement("span");
    heading.className = "eligible-cluster-heading";
    heading.append(
      textElement("strong", cluster.countryCode),
      textElement(
        "span",
        cluster.frpMegawatts === null
          ? "FRP unavailable"
          : `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(cluster.frpMegawatts)} MW FRP`,
        "eligible-cluster-frp"));
    button.append(
      heading,
      textElement(
        "span",
        `${cluster.source} · ${cluster.satellite} · ${formatDateTime(cluster.acquiredAtUtc)}`,
        "eligible-cluster-context"),
      textElement(
        "span",
        `${cluster.detectionCount} ${plural(cluster.detectionCount, "detection", "detections")} · ${formatDistance(cluster.clusterDiameterKilometers)} diameter`,
        "eligible-cluster-stats"),
      textElement(
        "span",
        `${formatSearchCoordinate(coordinate)} · Find representative →`,
        "eligible-cluster-coordinate"));
    item.append(button);
    return item;
  }

  function searchEligibleNotificationCluster(cluster) {
    try {
      const coordinate = mapSupport.eligibleNotificationClusterCoordinate(cluster);
      elements.coordinateSearchInput.value = formatSearchCoordinate(coordinate);
      searchCoordinates();
    } catch (error) {
      setCoordinateSearchFeedback("error", errorMessage(error));
    }
  }

  function searchCoordinates() {
    let coordinate;
    try {
      coordinate = mapSupport.parseCoordinateInput(elements.coordinateSearchInput.value);
    } catch (error) {
      setCoordinateSearchFeedback("error", errorMessage(error));
      return;
    }

    state.searchCoordinate = coordinate;
    saveCoordinateSearchToUrl(coordinate);
    selectNearestSearchResult();
    state.provider?.setSearchLocation(coordinate);
    state.provider?.focusCoordinate(coordinate);
  }

  function restoreCoordinateSearch() {
    let coordinate;
    try {
      coordinate = mapSupport.coordinateSearchFromUrl(window.location.href);
    } catch (error) {
      setCoordinateSearchFeedback("error", `Search link ignored. ${errorMessage(error)}`);
      return;
    }

    if (!coordinate)
      return;

    state.searchCoordinate = coordinate;
    elements.coordinateSearchInput.value = formatSearchCoordinate(coordinate);
    setCoordinateSearchFeedback(
      "success",
      `Restoring ${formatSearchCoordinate(coordinate)} from this link.`);
  }

  function saveCoordinateSearchToUrl(coordinate) {
    try {
      const url = mapSupport.urlWithCoordinateSearch(window.location.href, coordinate);
      window.history.replaceState(window.history.state, "", url);
      clearNotice("search-url");
    } catch {
      setNotice(
        "search-url",
        "warning",
        "The searched location could not be saved in the browser URL.");
    }
  }

  function selectNearestSearchResult() {
    const coordinate = state.searchCoordinate;
    if (!coordinate)
      return;

    const nearest = mapSupport.nearestCoordinatePoint(state.points, coordinate);
    if (!nearest) {
      clearSelection();
      renderNoAnomalies();
      setCoordinateSearchFeedback(
        "success",
        `Centered on ${formatSearchCoordinate(coordinate)} · no current anomaly is available to select.`);
      return;
    }

    setCoordinateSearchFeedback(
      "success",
      `Centered on ${formatSearchCoordinate(coordinate)} · nearest anomaly selected (${nearest.distanceKilometers.toFixed(1)} km away).`);
    selectPoint(nearest.point.key, true);
  }

  function setCoordinateSearchFeedback(kind, message) {
    elements.coordinateSearchInput.setAttribute("aria-invalid", kind === "error" ? "true" : "false");
    if (kind)
      elements.coordinateSearchFeedback.dataset.kind = kind;
    else
      delete elements.coordinateSearchFeedback.dataset.kind;
    elements.coordinateSearchFeedback.textContent = message;
  }

  function formatSearchCoordinate(coordinate) {
    return `${coordinate.latitude.toFixed(6)}, ${coordinate.longitude.toFixed(6)}`;
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
    const context = {
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
      provider.setSelection(state.selectedKey, state.clusterKeys);
      provider.setSearchLocation(state.searchCoordinate);
      if (state.searchCoordinate)
        provider.focusCoordinate(state.searchCoordinate);
      else
        provider.fitToAnomalies(state.points);
      state.provider = provider;
      elements.providerCaption.textContent = definition.caption();
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

  function selectPoint(key, fromCoordinateSearch = false) {
    const point = state.points.find(candidate => candidate.key === key);
    if (!point)
      return;

    if (state.searchCoordinate && !fromCoordinateSearch) {
      setCoordinateSearchFeedback(
        "success",
        `Search target remains at ${formatSearchCoordinate(state.searchCoordinate)} · selected a map anomaly.`);
    }

    state.selectedKey = key;
    resetDiagnostic();
    state.diagnosticStatus = "loading";
    state.provider?.setSelection(key, state.clusterKeys);
    renderDetails(point);
    void loadNotificationDiagnostic(point);
  }

  function clearSelection() {
    resetDiagnostic();
    state.selectedKey = null;
    state.provider?.setSelection(null, state.clusterKeys);
  }

  function renderNoAnomalies() {
    if (!state.snapshot) {
      renderEmptyDetails(
        "Anomalies unavailable",
        "The searched location is shown, but anomaly data is not currently available.");
      return;
    }

    const returnedItems = Array.isArray(state.snapshot?.items) ? state.snapshot.items : [];
    renderEmptyDetails(
      returnedItems.length === 0 ? "No current anomalies" : "No mappable anomalies",
      returnedItems.length === 0
        ? "The current API snapshot contains no heat anomalies."
        : "Every returned observation has malformed coordinates and was omitted from the map.");
  }

  async function loadNotificationDiagnostic(point) {
    const anomalyId = typeof point?.anomaly?.id === "string"
      ? point.anomaly.id.trim()
      : "";
    if (!anomalyId) {
      state.diagnosticStatus = "error";
      state.diagnosticError = "The selected observation has no anomaly ID.";
      renderSelectedDetails();
      return;
    }

    state.diagnosticAbortController?.abort();
    const controller = new AbortController();
    const version = ++state.diagnosticVersion;
    state.diagnosticAbortController = controller;
    try {
      const diagnostic = await fetchJson(
        `/api/viewer/notification-diagnostics/${encodeURIComponent(anomalyId)}`,
        controller.signal);
      if (version !== state.diagnosticVersion || state.selectedKey !== point.key)
        return;

      validateNotificationDiagnostic(diagnostic, anomalyId);
      state.diagnostic = diagnostic;
      state.diagnosticStatus = "ready";
      state.diagnosticError = null;
      state.clusterKeys = mapSupport.clusterPointKeys(state.points, diagnostic.memberIds);
      state.provider?.setSelection(state.selectedKey, state.clusterKeys);
      renderSelectedDetails();
    } catch (error) {
      if (error?.name === "AbortError"
          || version !== state.diagnosticVersion
          || state.selectedKey !== point.key) {
        return;
      }

      state.diagnostic = null;
      state.diagnosticStatus = "error";
      state.diagnosticError = errorMessage(error);
      state.clusterKeys = new Set();
      state.provider?.setSelection(state.selectedKey, state.clusterKeys);
      renderSelectedDetails();
    } finally {
      if (version === state.diagnosticVersion)
        state.diagnosticAbortController = null;
    }
  }

  function validateNotificationDiagnostic(diagnostic, anomalyId) {
    if (!diagnostic || typeof diagnostic !== "object"
        || diagnostic.selectedAnomalyId !== anomalyId
        || !Array.isArray(diagnostic.memberIds)
        || !Array.isArray(diagnostic.criteria)
        || !Array.isArray(diagnostic.nearbyFeatures)
        || typeof diagnostic.clusterId !== "string"
        || typeof diagnostic.representativeId !== "string"
        || typeof diagnostic.isEligible !== "boolean") {
      throw new Error("The notification diagnostic response is invalid.");
    }

    mapSupport.validateNearbyFeatures(diagnostic.nearbyFeatures);
  }

  function resetDiagnostic() {
    state.diagnosticAbortController?.abort();
    state.diagnosticAbortController = null;
    state.diagnosticVersion += 1;
    state.diagnostic = null;
    state.diagnosticStatus = "idle";
    state.diagnosticError = null;
    state.clusterKeys = new Set();
  }

  function renderSelectedDetails() {
    const selected = state.points.find(point => point.key === state.selectedKey);
    if (selected)
      renderDetails(selected);
  }

  function renderSnapshotSummary() {
    const returnedCount = state.snapshot.items.length;
    elements.countSummary.textContent = state.malformedCount > 0
      ? `${state.points.length} mapped · ${returnedCount} returned`
      : `${returnedCount} ${plural(returnedCount, "anomaly", "anomalies")}`;

    if (state.snapshot.isPartiallyStale === true) {
      elements.healthSummary.className = "status-value health-stale";
      elements.healthSummary.textContent = "Partial upstream data";
    } else if (state.snapshot.isReady === true) {
      elements.healthSummary.className = "status-value health-ready";
      elements.healthSummary.textContent = "Snapshot ready";
    } else {
      elements.healthSummary.className = "status-value";
      elements.healthSummary.textContent = "Snapshot warming up";
    }

    elements.generatedSummary.textContent = state.snapshot.generatedAtUtc
      ? `Generated ${formatDateTime(state.snapshot.generatedAtUtc)}`
      : "Generation time unavailable";
  }

  function renderUnavailableSummary() {
    elements.countSummary.textContent = "Anomalies unavailable";
    elements.healthSummary.className = "status-value";
    elements.healthSummary.textContent = "Unavailable";
    elements.generatedSummary.textContent = "No usable snapshot";
  }

  function renderEmptyDetails(title, message) {
    const wrapper = document.createElement("div");
    wrapper.className = "details-empty";
    wrapper.append(
      createEmptyOrbit(),
      textElement("p", "Anomaly inspector", "eyebrow"),
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
    const heading = document.createElement("header");
    heading.className = "details-heading";
    heading.append(
      textElement("p", "Selected anomaly", "eyebrow"),
      textElement("h2", anomaly.countryCode || "Heat anomaly"),
      textElement(
        "p",
        [anomaly.source, anomaly.satellite, formatDateTime(anomaly.acquiredAtUtc)]
          .filter(Boolean)
          .join(" · "),
        "details-subtitle"),
      detailsSummary(anomaly)
    );
    wrapper.append(heading);

    wrapper.append(notificationDiagnosticSection());

    const nearbyFeatures = nearbyFeatureSection();
    if (nearbyFeatures)
      wrapper.append(nearbyFeatures);

    const observationSection = section("Observation data");
    observationSection.append(fieldList(Object.entries(anomaly), point));
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

  function nearbyFeatureSection() {
    const nearbyFeatures = state.diagnosticStatus === "ready"
      && Array.isArray(state.diagnostic?.nearbyFeatures)
      ? state.diagnostic.nearbyFeatures
      : [];
    if (nearbyFeatures.length === 0)
      return null;

    const nearbySection = section("Possible nearby sources");
    nearbySection.append(textElement(
      "p",
      "Named OpenStreetMap features within 2 km of the selected anomaly, nearest first.",
      "details-subtitle"));
    const list = document.createElement("ul");
    list.className = "nearby-feature-list";
    nearbyFeatures.forEach(feature => {
      const item = document.createElement("li");
      const link = externalMapLink(
        mapSupport.googleMapsUrl(feature.latitude, feature.longitude),
        feature.name);
      link.className = "nearby-feature-link";
      link.textContent = feature.name;
      item.append(
        link,
        textElement("span", formatDistance(feature.distanceKilometers), "nearby-feature-distance"));
      list.append(item);
    });
    nearbySection.append(list);
    return nearbySection;
  }

  function notificationDiagnosticSection() {
    const diagnosticSection = section("Notification criteria");
    if (state.diagnosticStatus === "loading") {
      const loading = document.createElement("div");
      loading.className = "diagnostic-loading";
      loading.append(
        textElement("span", "", "spinner"),
        textElement("p", "Building the active-snapshot cluster and checking notification criteria…"));
      diagnosticSection.append(loading);
      return diagnosticSection;
    }

    if (state.diagnosticStatus === "error") {
      const error = document.createElement("div");
      error.className = "diagnostic-error";
      error.append(
        textElement("strong", "Notification diagnostics unavailable"),
        textElement("p", state.diagnosticError || "The diagnostic request failed."));
      diagnosticSection.append(error);
      return diagnosticSection;
    }

    if (state.diagnosticStatus !== "ready" || !state.diagnostic) {
      diagnosticSection.append(textElement(
        "p",
        "Select an anomaly to evaluate its current cluster.",
        "details-subtitle"));
      return diagnosticSection;
    }

    const diagnostic = state.diagnostic;
    const summary = document.createElement("div");
    summary.className = "diagnostic-summary";
    summary.append(
      textElement(
        "span",
        diagnostic.isEligible ? "Eligible now" : "Filtered out",
        `diagnostic-outcome ${diagnostic.isEligible ? "passed" : "failed"}`),
      textElement(
        "p",
        `${diagnostic.detectionCount} ${plural(diagnostic.detectionCount, "detection", "detections")} · ${formatDistance(diagnostic.clusterDiameterKilometers)} diameter`),
      textElement(
        "p",
        diagnostic.representativeId === diagnostic.selectedAnomalyId
          ? "The selected anomaly is the cluster representative."
          : "Another cluster member is the representative used by the criteria.",
        "details-subtitle"));
    diagnosticSection.append(summary);

    const list = document.createElement("ul");
    list.className = "criteria-list";
    diagnostic.criteria.forEach(criterion => list.append(criterionItem(criterion)));
    diagnosticSection.append(list);

    const disclosure = document.createElement("details");
    disclosure.className = "cluster-members";
    disclosure.append(textElement("summary", "Show cluster member IDs"));
    const members = document.createElement("ul");
    diagnostic.memberIds.forEach(memberId => {
      const suffix = memberId === diagnostic.representativeId ? " · representative" : "";
      members.append(textElement("li", `${memberId}${suffix}`));
    });
    disclosure.append(members);
    diagnosticSection.append(disclosure);
    return diagnosticSection;
  }

  function criterionItem(criterion) {
    const outcome = ["passed", "failed", "disabled", "unavailable"].includes(criterion?.outcome)
      ? criterion.outcome
      : "unavailable";
    const item = document.createElement("li");
    item.className = `criterion ${outcome}`;
    const heading = document.createElement("div");
    heading.className = "criterion-heading";
    heading.append(
      textElement("strong", criterion?.label || "Unnamed criterion"),
      textElement("span", outcome, "criterion-outcome"));
    item.append(
      heading,
      textElement("p", `Observed: ${criterion?.actualValue || "Not available"}`),
      textElement("p", `Required: ${criterion?.requirement || "Not available"}`),
      textElement("p", criterion?.explanation || "No explanation was returned.", "criterion-explanation"));
    return item;
  }

  function formatDistance(value) {
    return typeof value === "number" && Number.isFinite(value)
      ? `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value)} km`
      : "Unknown";
  }

  function section(title) {
    const element = document.createElement("section");
    element.className = "details-section";
    element.append(textElement("h3", title));
    return element;
  }

  function createEmptyOrbit() {
    const orbit = document.createElement("span");
    orbit.className = "empty-orbit";
    orbit.setAttribute("aria-hidden", "true");
    orbit.append(document.createElement("span"));
    return orbit;
  }

  function detailsSummary(anomaly) {
    const summary = document.createElement("div");
    summary.className = "details-summary";
    summary.append(
      summaryItem("Satellite", anomaly.satellite || "Not available"),
      summaryItem("FRP", typeof anomaly.frpMegawatts === "number"
        ? `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(anomaly.frpMegawatts)} MW`
        : "Not available"),
      summaryItem("Pass", anomaly.dayNight === "D"
        ? "Daytime"
        : anomaly.dayNight === "N" ? "Nighttime" : "Not available")
    );
    return summary;
  }

  function summaryItem(label, value) {
    const item = document.createElement("div");
    item.className = "summary-item";
    item.append(textElement("span", label), textElement("strong", value));
    return item;
  }

  function fieldList(entries, point = null) {
    const list = document.createElement("dl");
    list.className = "field-list";

    entries.forEach(([key, value]) => {
      list.append(textElement("dt", fieldLabels[key] ?? humanize(key)));
      const definition = document.createElement("dd");
      definition.append(formatFieldValue(key, value, point));
      list.append(definition);
    });
    return list;
  }

  function formatFieldValue(key, value, point) {
    if (value === null || value === undefined || value === "")
      return document.createTextNode("Not available");

    if (key === "googleMapsUrl" && typeof value === "string") {
      try {
        const url = new URL(value);
        if (url.protocol === "https:" || url.protocol === "http:") {
          const actions = document.createElement("span");
          actions.className = "map-actions";
          actions.append(
            externalMapLink(url.href, "Google Maps"),
            externalMapLink(
              mapSupport.yandexMapsUrl(point.latitude, point.longitude),
              "Yandex Maps")
          );
          return actions;
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

  function externalMapLink(url, label) {
    const link = document.createElement("a");
    link.className = "map-action";
    link.href = url;
    link.target = "_blank";
    link.rel = "noopener noreferrer";
    link.setAttribute("aria-label", `${label} (opens in a new tab)`);
    link.textContent = `${label} ↗`;
    return link;
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
    elements.coordinateSearchInput.disabled = busy;
    elements.coordinateSearchButton.disabled = busy;
    elements.refreshButton.disabled = busy;
    elements.refreshButtonLabel.textContent = busy ? "Loading…" : "Refresh data";
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

  function markerStyle(selected, clustered = false) {
    return mapSupport.notificationMarkerStyle(selected, clustered);
  }

  function searchMarkerStyle() {
    return mapSupport.coordinateSearchMarkerStyle();
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
      this.imageryLayer = null;
      this.searchMarker = null;
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

      const reportImageryResult = mapSupport.createGibsWarningReporter(context.onWarning);
      this.imageryLayer = window.L.gridLayer({
        attribution: '&copy; <a href="https://earthdata.nasa.gov/gibs">NASA GIBS</a>',
        bounds: [[-85.0511, -180], [85.0511, 180]],
        maxNativeZoom: 9,
        maxZoom: 14,
        noWrap: false
      });
      this.imageryLayer.createTile = (coordinates, done) => {
        const tile = document.createElement("img");
        tile.setAttribute("role", "presentation");
        tile.setAttribute("aria-hidden", "true");
        tile.cancelGibsLoad = mapSupport.loadGibsTile(tile, coordinates, {
          onComplete: result => {
            reportImageryResult(result);
            delete tile.cancelGibsLoad;
            done(null, tile);
          },
          onError: error => {
            reportImageryResult({ coverage: "none" });
            delete tile.cancelGibsLoad;
            done(error, tile);
          }
        });
        return tile;
      };
      this.imageryLayer.on("tileunload", event => event.tile.cancelGibsLoad?.());
      this.imageryLayer.addTo(this.map);

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

    setSelection(selectedKey, clusterKeys) {
      this.markers.forEach((marker, markerKey) => {
        const selected = markerKey === selectedKey;
        const visual = markerStyle(selected, clusterKeys.has(markerKey));
        marker.setStyle({
          radius: visual.size,
          color: visual.stroke,
          weight: visual.weight,
          fillColor: visual.fill
        });
        if (selected)
          marker.bringToFront();
      });
    }

    setSearchLocation(coordinate) {
      if (this.searchMarker) {
        this.searchMarker.remove();
        this.searchMarker = null;
      }
      if (!coordinate || !this.map)
        return;

      const visual = searchMarkerStyle();
      this.searchMarker = window.L.circleMarker([coordinate.latitude, coordinate.longitude], {
        radius: visual.size,
        color: visual.stroke,
        weight: visual.weight,
        fillColor: visual.fill,
        fillOpacity: 0.18,
        opacity: 1,
        bubblingMouseEvents: false
      });
      const tooltip = document.createElement("span");
      tooltip.textContent = `Searched coordinates · ${formatSearchCoordinate(coordinate)}`;
      this.searchMarker.bindTooltip(tooltip, { direction: "top", offset: [0, -6] });
      this.searchMarker.addTo(this.map);
      this.searchMarker.bringToBack();
    }

    focusCoordinate(coordinate) {
      this.map?.stop();
      this.map?.setView(
        [coordinate.latitude, coordinate.longitude],
        coordinateSearchZoom,
        { animate: false });
    }

    fitToAnomalies(points) {
      if (points.length === 0) {
        this.map.setView([20, 0], 2);
      } else if (points.length === 1) {
        this.map.setView([points[0].latitude, points[0].longitude], 8);
      } else {
        const bounds = window.L.latLngBounds(
          points.map(point => [point.latitude, point.longitude]));
        this.map.fitBounds(bounds, { animate: false, padding: [42, 42], maxZoom: 10 });
      }
    }

    resize() {
      this.map?.invalidateSize({ pan: false });
    }

    destroy() {
      if (this.map)
        this.map.remove();
      this.map = null;
      this.markerLayer = null;
      this.imageryLayer = null;
      this.searchMarker = null;
      this.markers.clear();
    }
  }

  class GoogleMapProvider {
    constructor() {
      this.map = null;
      this.anomalyLayer = null;
      this.onSelect = null;
      this.unsubscribeAuthFailure = null;
      this.authFailure = null;
      this.searchMarker = null;
    }

    async mount(container, context) {
      if (!context.googleApiKey)
        throw new Error("GOOGLE_MAPS_API_KEY is not configured.");

      this.onSelect = context.onSelect;
      this.unsubscribeAuthFailure = googleMapsLoader.subscribeToAuthenticationFailure(() => {
        this.authFailure = new Error("Google Maps rejected the configured API key.");
        context.onError(
          "Google Maps rejected GOOGLE_MAPS_API_KEY. Check API enablement, billing, and referrer restrictions.");
      });
      await googleMapsLoader.load(context.googleApiKey);
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

      this.anomalyLayer = mapSupport.createGoogleAnomalyLayer(this.map, {
        googleMaps: window.google.maps,
        onSelect: this.onSelect,
        labelForPoint: markerLabel,
        iconForState: googleMarkerIcon
      });
    }

    renderAnomalies(points) {
      this.anomalyLayer.render(points);
    }

    setSelection(selectedKey, clusterKeys) {
      this.anomalyLayer.setSelection(selectedKey, clusterKeys);
    }

    setSearchLocation(coordinate) {
      if (this.searchMarker) {
        window.google.maps.event.clearInstanceListeners(this.searchMarker);
        this.searchMarker.setMap(null);
        this.searchMarker = null;
      }
      if (!coordinate || !this.map)
        return;

      const visual = searchMarkerStyle();
      this.searchMarker = new window.google.maps.Marker({
        map: this.map,
        position: { lat: coordinate.latitude, lng: coordinate.longitude },
        title: `Searched coordinates · ${formatSearchCoordinate(coordinate)}`,
        clickable: false,
        icon: {
          path: window.google.maps.SymbolPath.CIRCLE,
          fillColor: visual.fill,
          fillOpacity: 0.18,
          scale: visual.size,
          strokeColor: visual.stroke,
          strokeOpacity: 1,
          strokeWeight: visual.weight
        },
        zIndex: 250
      });
    }

    focusCoordinate(coordinate) {
      if (!this.map)
        return;

      this.map.setCenter({ lat: coordinate.latitude, lng: coordinate.longitude });
      this.map.setZoom(coordinateSearchZoom);
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

    resize() {
      if (!this.map)
        return;

      const center = this.map.getCenter();
      window.google.maps.event.trigger(this.map, "resize");
      if (center)
        this.map.setCenter(center);
    }

    destroy() {
      if (window.google?.maps && this.searchMarker)
        window.google.maps.event.clearInstanceListeners(this.searchMarker);
      this.searchMarker?.setMap(null);
      this.searchMarker = null;
      this.anomalyLayer?.destroy();
      this.anomalyLayer = null;
      if (window.google?.maps && this.map)
        window.google.maps.event.clearInstanceListeners(this.map);
      this.unsubscribeAuthFailure?.();
      this.unsubscribeAuthFailure = null;
      this.map = null;
    }
  }

  function googleMarkerIcon(selected, clustered = false) {
    const visual = markerStyle(selected, clustered);
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

})();
