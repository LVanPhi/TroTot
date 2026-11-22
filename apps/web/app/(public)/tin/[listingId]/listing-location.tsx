"use client";

import "maplibre-gl/dist/maplibre-gl.css";

import Map, { Marker } from "react-map-gl/maplibre";

import { MAP_STYLE_URL } from "@/components/domain/location-picker";

/** Read-only map of the listing; attribution of OpenStreetMap stays visible. */
export function ListingLocation({ location }: { location: { lat: number; lng: number } }) {
  return (
    <div className="h-64 overflow-hidden rounded-xl border border-border">
      <Map
        initialViewState={{ latitude: location.lat, longitude: location.lng, zoom: 15 }}
        mapStyle={MAP_STYLE_URL}
        style={{ width: "100%", height: "100%" }}
      >
        <Marker latitude={location.lat} longitude={location.lng} />
      </Map>
    </div>
  );
}
