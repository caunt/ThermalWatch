# Telegram notification flow

> **Purpose:** Provide the end-to-end visual source map for how ThermalWatch selects, filters, illustrates, and sends Telegram notifications.
> **Scope:** Telegram enablement, automatic and manual candidate selection, clustering, representative choice, visibility and land-cover policy, GIBS imagery, in-memory state, formatting, delivery, and failure behavior.
> **Sources of truth:** [Notification service](../../src/ThermalWatch.Telegram/TelegramNotificationService.cs), [Telegram options](../../src/ThermalWatch.Telegram/TelegramOptions.cs), [visibility filter](../../src/ThermalWatch.Telegram/TelegramVisibilityFilter.cs), [land-cover filter](../../src/ThermalWatch.Telegram/TelegramLandCoverFilter.cs), [clustering](../../src/ThermalWatch.Core/NotificationClustering.cs), [GIBS client](../../src/ThermalWatch.Core/GibsClient.cs), [message formatter](../../src/ThermalWatch.Telegram/TelegramMessageFormatter.cs), and [host routes](../../src/ThermalWatch.Api/Program.cs).
> **Update when:** Telegram inputs, enablement, state, clustering, representative selection, filter order or thresholds, GIBS products or layer mapping, crop selection, preview policy, ranking, formatting, delivery, retry, endpoint behavior, or failure handling changes.

A FIRMS thermal anomaly is an observation of heat, not proof of a wildfire or an ongoing event. The Telegram pipeline is deliberately separate from the raw-observation API: none of the decisions below remove or annotate items returned by `/api/anomalies`.

Defaults are shown where they make a branch understandable. Every `TELEGRAM_*` value is configurable through the validated ranges in [operations](../operations.md). The [notification policy](../domain/notification-policy.md) explains the domain intent; the [Telegram notifier](telegram-notifier.md) explains the component boundaries.

```mermaid
flowchart TB
    START([ThermalWatch process starts])

    subgraph ENABLEMENT["1. Enable and validate the outbound Telegram client"]
        direction TB
        CREDENTIALS{"Both TELEGRAM_BOT_TOKEN<br/>and TELEGRAM_CHANNEL_ID present?"}
        NOT_CONFIGURED["Neither present:<br/>Telegram hosted service is not started"]
        PARTIAL["Only one present:<br/>warn and do not start Telegram hosted service"]
        BUILD_CLIENT["Create Telegram bot client<br/>HTTP resilience plus bot-client retries"]
        VALIDATE_BOT["GetMe: resolve bot identity"]
        VALIDATE_CHAT["GetChat: configured destination<br/>must be a Telegram channel"]
        VALIDATE_MEMBER["GetChatMember: bot must be owner,<br/>or administrator allowed to post"]
        VALIDATION_OK{"All startup validation passed?"}
        DISABLED["Disable Telegram for this process lifetime<br/>Polling and HTTP API continue"]
        VALIDATED["Publish validated bot client and channel ID<br/>Automatic and manual paths are available"]
    end

    subgraph INPUTS["2. FIRMS observations available to notification selection"]
        direction TB
        FIRMS_FEEDS["Immutable active snapshot contains four distinct feeds:<br/>MODIS_NRT - MODIS on Terra or Aqua<br/>VIIRS_SNPP_NRT - VIIRS on Suomi-NPP<br/>VIIRS_NOAA20_NRT - VIIRS on NOAA-20<br/>VIIRS_NOAA21_NRT - VIIRS on NOAA-21"]
        OBSERVATION_FIELDS["Each observation supplies source, satellite, instrument,<br/>country, coordinates, UTC acquisition, day/night,<br/>FRP, confidence, brightness values, and stable anomaly ID"]
        SNAPSHOT_STREAM["AnomalySnapshotStore publishes immutable updates<br/>to one Telegram consumer"]
        READY{"Snapshot IsReady?<br/>At least one configured segment succeeded"}
        WAIT_READY["Ignore this update<br/>and await the next snapshot"]
    end

    subgraph AUTOMATIC["3. Automatic path: find genuinely new observations"]
        direction TB
        EXPIRE_SEEN["Expire seen IDs older than TELEGRAM_SEEN_RETENTION<br/>default 48 h; trim oldest above hard cap 100,000"]
        FIRST_READY{"First ready snapshot and<br/>notify-existing-on-startup is false?"}
        PRIME_SEEN["Mark every current observation seen<br/>Do not notify for the startup backlog"]
        FIND_NEW["Select observations whose stable IDs are not seen"]
        MARK_SEEN["Mark every new ID seen before filtering or sending<br/>Rejected and failed candidates do not become new again"]
        HAS_NEW{"Any new observations?"}
        CONTEXT_MODE{"Visibility filter enabled and<br/>minimum cluster detections greater than 1?"}
        NEW_ONLY["Cluster distinct new observations only"]
        ACTIVE_CONTEXT["Add active observations directly related to a new one<br/>within radius and time window; an older context member<br/>may become representative"]
        CLUSTER_AUTO["Build connected components using links within<br/>radius default 5 km and time separation default 90 min<br/>Keep only clusters containing at least one new ID"]
        AUTO_PENDING_SCAN["Visit the next in-memory pending entry after each ready snapshot<br/>When the list is exhausted, await another snapshot update"]
    end

    subgraph CLUSTERING["4. Shared cluster meaning and representative selection"]
        direction TB
        CLUSTER_MEMBERS["Links are transitive across countries, feeds, and satellites<br/>so total cluster diameter may exceed the link radius"]
        SORT_MEMBERS["Sort members by newest acquisition, then anomaly ID"]
        REPRESENTATIVE["Choose one representative:<br/>1 highest available FRP; missing ranks last<br/>2 newest acquisition<br/>3 lexically smallest anomaly ID"]
        CLUSTER_ID["Hash sorted member IDs into deterministic cluster ID<br/>Representative drives metadata gates, imagery, location, and map link<br/>All members drive count, coverage, and multi-satellite facts"]
    end

    subgraph VISIBILITY["5. Ordered visibility metadata filter"]
        direction TB
        VIS_ENABLED{"TELEGRAM_VISIBILITY_FILTER_ENABLED?<br/>default true"}
        DAYTIME{"Daytime not required,<br/>or representative pass is D?<br/>default requires daytime"}
        MEMBER_COUNT{"Member count meets minimum?<br/>default at least 2"}
        SOURCE_KIND{"Representative source is MODIS_NRT?"}
        MODIS_CONFIDENCE{"Numeric MODIS confidence present<br/>and at least configured percent?<br/>default 60 percent"}
        VIIRS_CONFIDENCE{"VIIRS confidence category recognized<br/>and at least configured level?<br/>low less than nominal less than high; default nominal"}
        FRP_GATE{"Minimum FRP is zero,<br/>or representative FRP is present<br/>and meets threshold? default 50 MW"}
        CONTRAST_GATE{"Minimum contrast is zero,<br/>or primary minus secondary brightness is present<br/>and meets threshold? default 20 K"}
        VIS_REJECT["Reject cluster with first matching reason:<br/>nighttime, insufficient detections, low or missing confidence,<br/>low or missing FRP, or low or missing thermal contrast"]
        REJECTION_ORIGIN{"Rejected automatic<br/>or manual candidate?"}
        VIS_PASS["Visibility metadata accepted<br/>or the visibility filter is disabled"]
    end

    subgraph LAND_COVER["6. Optional NASA land-cover filter over all cluster members"]
        direction TB
        LAND_ENABLED{"TELEGRAM_LAND_COVER_FILTER_ENABLED?<br/>default true"}
        LAND_PRODUCT["NASA GIBS annual combined MODIS product:<br/>MODIS_Combined_L3_IGBP_Land_Cover_Type_Annual<br/>500 m matrix, indexed PNG tiles"]
        LAND_PIXELS["For every member, collect its land-cover pixel<br/>plus pixels intersecting built-up proximity<br/>default 2 km; hard limit 1,000,000 pixels"]
        LAND_DATE["Find newest annual date common to every required tile<br/>Fetch and decode official indexed colors to IGBP classes"]
        LAND_CACHE["Shared host memory cache is capped at 64 MiB<br/>Date domains: success 12 h, miss 5 min<br/>Decoded tiles: success 24 h, miss 5 min"]
        LAND_VALID{"All dates, tiles, pixels, year,<br/>and decoded classes 1 through 17 valid?"}
        LAND_FAIL_OPEN["Land cover unavailable or invalid:<br/>retain cluster and format as unavailable<br/>This filter fails open"]
        VEG_PERCENT["Vegetation percent across sampled pixels<br/>IGBP classes 1 through 12 and 14 are vegetation<br/>Class 13 is urban or built-up"]
        VEG_THRESHOLD{"Vegetation below threshold?<br/>default threshold 50 percent"}
        BUILT_UP{"Any class 13 pixel within proximity?"}
        HIGH_FRP_EXCEPTION{"High-FRP vegetation exception enabled<br/>and representative FRP at least threshold?<br/>default exception off; threshold 300 MW"}
        MULTI_EXCEPTION{"Multi-satellite vegetation exception enabled<br/>and members contain multiple satellite names?<br/>default exception off"}
        LAND_RETAIN["Retain cluster and carry land-cover summary"]
        LAND_SUPPRESS["Suppress vegetation-dominated cluster<br/>Seen IDs remain seen; API observations remain unchanged"]
    end

    subgraph PREVIEW["7. Select coverage and request sensor-matched NASA GIBS imagery"]
        direction TB
        DIAMETER["Compute greatest pairwise cluster diameter"]
        LARGE_PREVIEW{"Large if any condition is met:<br/>members at least 8, representative FRP at least 500 MW,<br/>or diameter at least 8 km"}
        NORMAL_CROP["Normal default coverage 30 x 20 km<br/>Output 900 x 600 pixels"]
        LARGE_CROP["Large default coverage 45 x 30 km<br/>Output 900 x 600 pixels"]
        CANDIDATE_ORIGIN{"Candidate came from automatic<br/>or manual processing?"}
        QUEUE_AUTO["Automatic: append in-memory pending entry<br/>with cluster, first-seen time, dimensions, and land-cover summary"]
        FETCH_PREVIEW["Center bounds on representative coordinates<br/>Use representative UTC acquisition date, not exact minute"]
        LAYER_SOURCE{"Representative FIRMS source and pass"}
        MODIS_TERRA["MODIS Terra<br/>Day base: CorrectedReflectance TrueColor, 250 m<br/>Night base: Brightness Temp Band31 Night, 1 km<br/>Overlay: Thermal Anomalies All, 1 km"]
        MODIS_AQUA["MODIS Aqua<br/>Day base: CorrectedReflectance TrueColor, 250 m<br/>Night base: Brightness Temp Band31 Night, 1 km<br/>Overlay: Thermal Anomalies All, 1 km"]
        VIIRS_SNPP["VIIRS Suomi-NPP<br/>Day base: CorrectedReflectance TrueColor, 250 m<br/>Night base: Brightness Temp BandI5 Night, 250 m<br/>Overlay: Thermal Anomalies 375m All, 500 m matrix"]
        VIIRS_NOAA20["VIIRS NOAA-20<br/>Day base: CorrectedReflectance TrueColor, 250 m<br/>Night base: Brightness Temp BandI5 Night, 250 m<br/>Overlay: Thermal Anomalies 375m All, 500 m matrix"]
        VIIRS_NOAA21["VIIRS NOAA-21<br/>Day base: CorrectedReflectance TrueColor, 250 m<br/>Night base: Brightness Temp BandI5 Night, 250 m<br/>Overlay: Thermal Anomalies 375m All, 500 m matrix"]
        LAYERS_KNOWN{"Source and MODIS satellite map to known layers,<br/>and geographic bounds are valid?"}
        EXACT_DATE{"Both base and anomaly overlay advertise<br/>the exact representative acquisition date?"}
        WMS["Request WMS 1.1.1 EPSG:4326 PNG composite<br/>with base plus size5 thermal-anomaly overlay<br/>Never substitute nearest date, other sensor, or other pass"]
        PNG_VALID{"Successful bounded image response<br/>with valid non-empty PNG, at most 10 MiB?"}
        PREVIEW_READY["Preview available<br/>Cache PNG for 2 h"]
        PREVIEW_MISSING["Preview unavailable<br/>Layer availability caches: success 12 h, miss 5 min"]
        PREVIEW_DESTINATION{"Automatic pending entry<br/>or manual candidate?"}
    end

    subgraph AUTO_PREVIEW_POLICY["8. Automatic preview wait and retry policy"]
        direction TB
        AUTO_PREVIEW_RESULT{"Preview available?"}
        RETRY_EXPIRED{"Time since pending first-seen reaches<br/>TELEGRAM_PREVIEW_RETRY_WINDOW? default 1 h"}
        KEEP_PENDING["Keep pending entry untouched<br/>No independent timer: retry after a later snapshot update"]
        AUTO_REQUIRE_PREVIEW{"Visibility filter enabled<br/>and preview required? default true"}
        PREVIEW_TIMEOUT_DROP["Discard pending cluster after timeout<br/>Record preview-unavailable rejection"]
        AUTO_PHOTO["Prepare Telegram photo plus HTML caption"]
        AUTO_TEXT["Preview fallback allowed:<br/>prepare HTML text message with link previews disabled"]
    end

    subgraph DELIVERY["9. Shared message construction and Telegram delivery"]
        direction TB
        FORMAT_MESSAGE["Choose single- or multi-satellite template<br/>Single: representative time, pass, confidence, FRP, and contrast<br/>Multi: latest time, peak FRP and contrast, distinct countries, feeds, satellites<br/>Both: member count, diameter, representative coordinates, imagery facts<br/>HTML-encode and compact progressively to 1,024 characters<br/>Always state that the observation is not a confirmed event"]
        LOCATION_BUTTON["Attach inline Open in Google Maps button<br/>using representative coordinates"]
        SEND_KIND{"PNG preview available?"}
        SEND_PHOTO["SendPhoto: thermal-anomaly.png<br/>with HTML caption and keyboard"]
        SEND_TEXT["SendMessage: HTML text and keyboard<br/>Telegram link preview disabled"]
        DELIVERY_ORIGIN{"Automatic or manual delivery?"}
        AUTO_SUCCESS["Automatic success:<br/>remove pending entry and count accepted"]
        AUTO_PERMANENT{"Automatic Telegram API 400, 401, or 403?"}
        AUTO_DISABLE["Clear validated state, disable automatic notifier,<br/>and stop processing until process restart"]
        AUTO_TRANSIENT["Other automatic failure:<br/>leave pending entry and retry on a later snapshot"]
        MANUAL_SUCCESS["Manual success:<br/>increment sent count"]
        MANUAL_FAILURE["Manual candidate failure:<br/>record cluster ID and continue with later candidates"]
        MANUAL_ACCOUNT["Update manual sent or failed counters"]
        MORE_MANUAL{"More selected manual candidates?"}
    end

    subgraph MANUAL["10. Manual GET /api/telegram/send-top path"]
        direction TB
        MANUAL_REQUEST["Unauthenticated side-effecting GET request<br/>count defaults to 5; valid range 1 through 50"]
        MANUAL_AVAILABLE{"Validated Telegram client still available?"}
        MANUAL_CONFLICT["Return 409: Telegram unavailable"]
        MANUAL_GATE{"Acquire nonblocking single-operation semaphore?"}
        MANUAL_BUSY["Return 409: another manual send is running"]
        CURRENT_SNAPSHOT["Read current in-memory snapshot only<br/>Do not refresh FIRMS or wait for readiness"]
        CLUSTER_MANUAL["Treat every current item as new input<br/>Cluster without older active-context expansion<br/>Do not read or mutate automatic seen IDs or pending entries"]
        MANUAL_PREVIEW_RESULT{"Preview available?"}
        MANUAL_REQUIRE_PREVIEW{"Visibility filter enabled<br/>and preview required?"}
        MANUAL_SKIP["Skip candidate immediately<br/>Manual path has no preview retry window"]
        MANUAL_ELIGIBLE["Add filtered candidate with preview or allowed text fallback"]
        RANK["After all clusters are evaluated, rank eligible clusters by:<br/>available then highest representative FRP,<br/>member count, diameter, acquisition time, then cluster ID<br/>Take requested count"]
        ANY_SELECTED{"Any candidates selected?"}
        NO_ANOMALIES_STATUS["Send No anomalies currently pass all filters status"]
        INTRO_STATUS["Send introductory top-count status message"]
        STATUS_SENT{"Status message sent?"}
        STATUS_FAILED["Return 502-style StatusMessageFailed result<br/>Do not send selected candidates; release semaphore in finally"]
        SEND_EACH["Take and send the next selected candidate independently"]
        MANUAL_RESPONSE["Return requested, eligible, selected, sent,<br/>failed counts and failed cluster IDs<br/>Release semaphore"]
    end

    API_BOUNDARY["Invariant: Telegram filters, seen state, pending state,<br/>land cover, previews, and delivery never modify /api/anomalies"]

    START --> CREDENTIALS
    CREDENTIALS -- "Both" --> BUILD_CLIENT
    CREDENTIALS -- "Neither" --> NOT_CONFIGURED
    CREDENTIALS -- "Partial" --> PARTIAL
    BUILD_CLIENT --> VALIDATE_BOT --> VALIDATE_CHAT --> VALIDATE_MEMBER --> VALIDATION_OK
    VALIDATION_OK -- "No" --> DISABLED
    VALIDATION_OK -- "Yes" --> VALIDATED

    VALIDATED --> FIRMS_FEEDS --> OBSERVATION_FIELDS --> SNAPSHOT_STREAM --> READY
    READY -- "No" --> WAIT_READY --> SNAPSHOT_STREAM
    READY -- "Yes" --> EXPIRE_SEEN --> FIRST_READY
    FIRST_READY -- "Yes" --> PRIME_SEEN --> AUTO_PENDING_SCAN
    FIRST_READY -- "No" --> FIND_NEW --> MARK_SEEN --> HAS_NEW
    HAS_NEW -- "No" --> AUTO_PENDING_SCAN
    HAS_NEW -- "Yes" --> CONTEXT_MODE
    CONTEXT_MODE -- "No" --> NEW_ONLY --> CLUSTER_AUTO
    CONTEXT_MODE -- "Yes" --> ACTIVE_CONTEXT --> CLUSTER_AUTO

    CLUSTER_AUTO --> CLUSTER_MEMBERS
    CLUSTER_MANUAL --> CLUSTER_MEMBERS
    CLUSTER_MEMBERS --> SORT_MEMBERS --> REPRESENTATIVE --> CLUSTER_ID --> VIS_ENABLED
    VIS_ENABLED -- "No" --> VIS_PASS
    VIS_ENABLED -- "Yes" --> DAYTIME
    DAYTIME -- "No" --> VIS_REJECT
    DAYTIME -- "Yes" --> MEMBER_COUNT
    MEMBER_COUNT -- "No" --> VIS_REJECT
    MEMBER_COUNT -- "Yes" --> SOURCE_KIND
    SOURCE_KIND -- "Yes" --> MODIS_CONFIDENCE
    SOURCE_KIND -- "No: VIIRS" --> VIIRS_CONFIDENCE
    MODIS_CONFIDENCE -- "No" --> VIS_REJECT
    VIIRS_CONFIDENCE -- "No" --> VIS_REJECT
    MODIS_CONFIDENCE -- "Yes" --> FRP_GATE
    VIIRS_CONFIDENCE -- "Yes" --> FRP_GATE
    FRP_GATE -- "No" --> VIS_REJECT
    FRP_GATE -- "Yes" --> CONTRAST_GATE
    CONTRAST_GATE -- "No" --> VIS_REJECT
    CONTRAST_GATE -- "Yes" --> VIS_PASS

    VIS_PASS --> LAND_ENABLED
    LAND_ENABLED -- "No" --> LAND_RETAIN
    LAND_ENABLED -- "Yes" --> LAND_PRODUCT --> LAND_PIXELS --> LAND_DATE --> LAND_CACHE --> LAND_VALID
    LAND_VALID -- "No" --> LAND_FAIL_OPEN --> LAND_RETAIN
    LAND_VALID -- "Yes" --> VEG_PERCENT --> VEG_THRESHOLD
    VEG_THRESHOLD -- "Yes" --> LAND_RETAIN
    VEG_THRESHOLD -- "No" --> BUILT_UP
    BUILT_UP -- "Yes" --> LAND_RETAIN
    BUILT_UP -- "No" --> HIGH_FRP_EXCEPTION
    HIGH_FRP_EXCEPTION -- "Yes" --> LAND_RETAIN
    HIGH_FRP_EXCEPTION -- "No" --> MULTI_EXCEPTION
    MULTI_EXCEPTION -- "Yes" --> LAND_RETAIN
    MULTI_EXCEPTION -- "No" --> LAND_SUPPRESS

    LAND_RETAIN --> DIAMETER --> LARGE_PREVIEW
    LARGE_PREVIEW -- "No" --> NORMAL_CROP --> CANDIDATE_ORIGIN
    LARGE_PREVIEW -- "Yes" --> LARGE_CROP --> CANDIDATE_ORIGIN
    CANDIDATE_ORIGIN -- "Automatic" --> QUEUE_AUTO --> AUTO_PENDING_SCAN
    CANDIDATE_ORIGIN -- "Manual" --> FETCH_PREVIEW
    AUTO_PENDING_SCAN --> FETCH_PREVIEW
    FETCH_PREVIEW --> LAYER_SOURCE
    LAYER_SOURCE -- "MODIS_NRT plus Terra or T" --> MODIS_TERRA --> LAYERS_KNOWN
    LAYER_SOURCE -- "MODIS_NRT plus Aqua or A" --> MODIS_AQUA --> LAYERS_KNOWN
    LAYER_SOURCE -- "VIIRS_SNPP_NRT" --> VIIRS_SNPP --> LAYERS_KNOWN
    LAYER_SOURCE -- "VIIRS_NOAA20_NRT" --> VIIRS_NOAA20 --> LAYERS_KNOWN
    LAYER_SOURCE -- "VIIRS_NOAA21_NRT" --> VIIRS_NOAA21 --> LAYERS_KNOWN
    LAYER_SOURCE -- "Anything else" --> PREVIEW_MISSING
    LAYERS_KNOWN -- "No" --> PREVIEW_MISSING
    LAYERS_KNOWN -- "Yes" --> EXACT_DATE
    EXACT_DATE -- "No" --> PREVIEW_MISSING
    EXACT_DATE -- "Yes" --> WMS --> PNG_VALID
    PNG_VALID -- "No" --> PREVIEW_MISSING
    PNG_VALID -- "Yes" --> PREVIEW_READY

    PREVIEW_READY --> PREVIEW_DESTINATION
    PREVIEW_MISSING --> PREVIEW_DESTINATION
    PREVIEW_DESTINATION -- "Automatic" --> AUTO_PREVIEW_RESULT
    AUTO_PREVIEW_RESULT -- "Yes" --> AUTO_PHOTO --> FORMAT_MESSAGE
    AUTO_PREVIEW_RESULT -- "No" --> RETRY_EXPIRED
    RETRY_EXPIRED -- "No" --> KEEP_PENDING -. "continue later entries; retry this one next update" .-> AUTO_PENDING_SCAN
    RETRY_EXPIRED -- "Yes" --> AUTO_REQUIRE_PREVIEW
    AUTO_REQUIRE_PREVIEW -- "Yes" --> PREVIEW_TIMEOUT_DROP
    PREVIEW_TIMEOUT_DROP --> AUTO_PENDING_SCAN
    AUTO_REQUIRE_PREVIEW -- "No" --> AUTO_TEXT --> FORMAT_MESSAGE

    PREVIEW_DESTINATION -- "Manual" --> MANUAL_PREVIEW_RESULT
    MANUAL_PREVIEW_RESULT -- "Yes" --> MANUAL_ELIGIBLE
    MANUAL_PREVIEW_RESULT -- "No" --> MANUAL_REQUIRE_PREVIEW
    MANUAL_REQUIRE_PREVIEW -- "Yes" --> MANUAL_SKIP
    MANUAL_REQUIRE_PREVIEW -- "No" --> MANUAL_ELIGIBLE
    MANUAL_ELIGIBLE --> RANK
    MANUAL_SKIP --> RANK

    START -. "endpoint is always mapped" .-> MANUAL_REQUEST --> MANUAL_AVAILABLE
    MANUAL_AVAILABLE -- "No" --> MANUAL_CONFLICT
    MANUAL_AVAILABLE -- "Yes" --> MANUAL_GATE
    MANUAL_GATE -- "No" --> MANUAL_BUSY
    MANUAL_GATE -- "Yes" --> CURRENT_SNAPSHOT --> CLUSTER_MANUAL
    CLUSTER_MANUAL -. "empty snapshot" .-> RANK
    VIS_REJECT --> REJECTION_ORIGIN
    LAND_SUPPRESS --> REJECTION_ORIGIN
    REJECTION_ORIGIN -- "Manual" --> MANUAL_SKIP
    REJECTION_ORIGIN -- "Automatic" --> AUTO_PENDING_SCAN
    RANK --> ANY_SELECTED
    ANY_SELECTED -- "No" --> NO_ANOMALIES_STATUS --> STATUS_SENT
    ANY_SELECTED -- "Yes" --> INTRO_STATUS --> STATUS_SENT
    STATUS_SENT -- "No" --> STATUS_FAILED
    STATUS_SENT -- "Yes, candidates selected" --> SEND_EACH --> FORMAT_MESSAGE
    STATUS_SENT -- "Yes, none selected" --> MANUAL_RESPONSE

    FORMAT_MESSAGE --> LOCATION_BUTTON --> SEND_KIND
    SEND_KIND -- "Yes" --> SEND_PHOTO --> DELIVERY_ORIGIN
    SEND_KIND -- "No" --> SEND_TEXT --> DELIVERY_ORIGIN
    DELIVERY_ORIGIN -- "Automatic success" --> AUTO_SUCCESS --> AUTO_PENDING_SCAN
    DELIVERY_ORIGIN -- "Automatic failure" --> AUTO_PERMANENT
    AUTO_PERMANENT -- "Yes" --> AUTO_DISABLE
    AUTO_PERMANENT -- "No" --> AUTO_TRANSIENT -. "next ready snapshot" .-> AUTO_PENDING_SCAN
    DELIVERY_ORIGIN -- "Manual success" --> MANUAL_SUCCESS --> MANUAL_ACCOUNT
    DELIVERY_ORIGIN -- "Manual failure" --> MANUAL_FAILURE --> MANUAL_ACCOUNT
    MANUAL_ACCOUNT --> MORE_MANUAL
    MORE_MANUAL -- "Yes" --> SEND_EACH
    MORE_MANUAL -- "No" --> MANUAL_RESPONSE

    FIRMS_FEEDS -.-> API_BOUNDARY
    VIS_REJECT -.-> API_BOUNDARY
    LAND_SUPPRESS -.-> API_BOUNDARY
    PREVIEW_TIMEOUT_DROP -.-> API_BOUNDARY
    AUTO_DISABLE -.-> API_BOUNDARY

    classDef accepted fill:#e7f7ed,stroke:#218739,color:#123b1e;
    classDef rejected fill:#fdeaea,stroke:#bd2c2c,color:#561515;
    classDef external fill:#eaf2ff,stroke:#3568a8,color:#173552;
    classDef state fill:#fff5d9,stroke:#a47713,color:#4f3909;
    class VALIDATED,VIS_PASS,LAND_RETAIN,PREVIEW_READY,AUTO_SUCCESS,MANUAL_SUCCESS accepted;
    class NOT_CONFIGURED,PARTIAL,DISABLED,VIS_REJECT,LAND_SUPPRESS,PREVIEW_TIMEOUT_DROP,AUTO_DISABLE,MANUAL_CONFLICT,MANUAL_BUSY,MANUAL_SKIP,STATUS_FAILED rejected;
    class FIRMS_FEEDS,LAND_PRODUCT,MODIS_TERRA,MODIS_AQUA,VIIRS_SNPP,VIIRS_NOAA20,VIIRS_NOAA21,WMS,SEND_PHOTO,SEND_TEXT external;
    class EXPIRE_SEEN,PRIME_SEEN,MARK_SEEN,QUEUE_AUTO,AUTO_PENDING_SCAN,KEEP_PENDING state;
```

The diagram intentionally shows nighttime base layers even though the default visibility policy requires daytime observations: those layers become reachable when daytime is not required or when the visibility filter is disabled. Likewise, text-only delivery is reachable only when the preview requirement does not reject an unavailable preview.
