(() => {
  "use strict";

  let currentMap = null;

  const send = message => window.HybridWebView.SendRawMessage(JSON.stringify(message));

  const createElement = (tagName, className, text) => {
    const element = document.createElement(tagName);
    if (className) element.className = className;
    if (text) element.textContent = text;
    return element;
  };

  const createClusterIcon = cluster => {
    const count = cluster.getChildCount();
    const knownCount = cluster.getAllChildMarkers().filter(marker => marker.options.isKnown).length;
    const tone = knownCount === 0 ? "new" : knownCount === count ? "known" : "mixed";
    const size = count < 10 ? "small" : count < 100 ? "medium" : "large";
    const diameter = size === "small" ? 40 : size === "medium" ? 50 : 60;
    const content = createElement("div");
    content.appendChild(createElement("span", null, count.toString()));

    return L.divIcon({
      html: content,
      className: `marker-cluster marker-cluster-${size} marker-cluster--${tone}`,
      iconSize: L.point(diameter, diameter)
    });
  };

  const createPhotoIcon = sighting => {
    const root = createElement("div", `photo-pin${sighting.isKnown ? " photo-pin--known" : ""}`);
    if (sighting.image) {
      const image = createElement("img", "photo-pin__image");
      image.src = sighting.image;
      image.alt = "";
      root.appendChild(image);
    } else {
      root.appendChild(createElement("div", "photo-pin__fallback", "CAR"));
    }

    root.appendChild(createElement("div", "photo-pin__plate", sighting.displayPlate));
    if (sighting.price) {
      root.appendChild(createElement("div", "photo-pin__price", sighting.price));
    }

    return L.divIcon({
      className: "",
      html: root,
      iconSize: [58, 52],
      iconAnchor: [29, 45],
      popupAnchor: [0, -44]
    });
  };

  const createPopup = (sighting, canOpenVehicleHistory) => {
    const popup = createElement("div", "popup");
    if (sighting.image) {
      const image = createElement("img");
      image.src = sighting.image;
      image.alt = "Vehicle snapshot";
      popup.appendChild(image);
    }

    popup.appendChild(createElement("div", "popup__plate", sighting.displayPlate));
    const details = [
      sighting.vehicleName,
      sighting.seen,
      sighting.confidence,
      sighting.accuracyMeters == null ? null : `GPS accuracy ±${Math.round(sighting.accuracyMeters)} m`
    ];
    details.filter(Boolean).forEach(value => popup.appendChild(createElement("div", "popup__meta", value)));

    if (canOpenVehicleHistory) {
      const button = createElement("button", null, "View vehicle history");
      button.type = "button";
      button.addEventListener("click", () => send({ type: "vehicle", plate: sighting.normalizedPlate }));
      popup.appendChild(button);
    }

    return popup;
  };

  const render = payload => {
    if (currentMap) {
      currentMap.remove();
      currentMap = null;
    }

    const mapElement = document.getElementById("map");
    mapElement.replaceChildren();
    const tileWarning = document.getElementById("tile-warning");
    tileWarning.style.display = "none";
    const interactive = payload.isInteractive;
    const map = L.map(mapElement, {
      zoomControl: interactive,
      preferCanvas: true,
      attributionControl: true,
      dragging: interactive,
      scrollWheelZoom: interactive,
      doubleClickZoom: interactive,
      boxZoom: interactive,
      keyboard: interactive,
      touchZoom: interactive
    });
    currentMap = map;

    let tileErrors = 0;
    L.tileLayer(payload.tileUrl, {
      maxZoom: 19,
      attribution: payload.attribution,
      crossOrigin: true,
      updateWhenIdle: true,
      keepBuffer: 2
    })
      .on("tileerror", () => {
        tileErrors += 1;
        if (tileErrors === 3) tileWarning.style.display = "block";
      })
      .addTo(map);

    const bounds = [];
    if (payload.route.length) {
      payload.route.forEach(point => bounds.push(point));
      L.polyline(payload.route, {
        color: "#20b99a",
        weight: 6,
        opacity: 0.95,
        lineCap: "round",
        lineJoin: "round"
      }).addTo(map);

      const endpoint = kind => L.divIcon({
        className: "",
        html: createElement("div", `endpoint endpoint--${kind}`),
        iconSize: [24, 24],
        iconAnchor: [12, 12]
      });
      const start = L.marker(payload.route[0], { icon: endpoint("start"), zIndexOffset: 500 }).addTo(map);
      const finish = L.marker(payload.route[payload.route.length - 1], { icon: endpoint("finish"), zIndexOffset: 500 }).addTo(map);
      if (interactive) {
        start.bindTooltip("Trip start");
        finish.bindTooltip("Trip finish");
      }
    }

    const clusters = L.markerClusterGroup({
      showCoverageOnHover: false,
      maxClusterRadius: 52,
      spiderfyOnMaxZoom: interactive,
      removeOutsideVisibleBounds: false,
      iconCreateFunction: createClusterIcon
    });
    payload.sightings.forEach(sighting => {
      const marker = L.marker([sighting.latitude, sighting.longitude], {
        isKnown: sighting.isKnown,
        icon: createPhotoIcon(sighting)
      });
      if (interactive) {
        marker.bindPopup(createPopup(sighting, payload.canOpenVehicleHistory), {
          maxWidth: 300,
          maxHeight: Math.max(120, map.getSize().y - 104),
          autoPan: true,
          keepInView: false,
          autoPanPadding: [16, 16]
        });
      }

      clusters.addLayer(marker);
      bounds.push([sighting.latitude, sighting.longitude]);
    });
    map.addLayer(clusters);

    if (bounds.length === 1) {
      map.setView(bounds[0], 16);
    } else {
      map.fitBounds(bounds, { padding: [38, 38], maxZoom: 17 });
    }

    requestAnimationFrame(() => {
      map.invalidateSize();
      send({ type: "map-ready" });
    });
  };

  window.addEventListener("HybridWebViewMessageReceived", event => {
    try {
      const message = JSON.parse(event.detail.message);
      if (message.type === "render") render(message.payload);
    } catch (error) {
      send({ type: "error", message: error instanceof Error ? error.message : String(error) });
    }
  });

  document.addEventListener("DOMContentLoaded", () => send({ type: "web-ready" }));
})();
