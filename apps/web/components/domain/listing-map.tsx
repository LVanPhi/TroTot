"use client";

import "maplibre-gl/dist/maplibre-gl.css";

import { Search } from "lucide-react";
import { useState } from "react";
import Map, { Marker, NavigationControl, Popup } from "react-map-gl/maplibre";

import { ListingCard, type ListingItem } from "./listing-card";
import { MAP_STYLE_URL } from "./location-picker";

/**
 * Markers for the current result page (≤ 50); a click opens the card. Moving the map only shows
 * "Tìm ở khu vực này" — the API is not called on every drag (docs/06-frontend.md, Bản đồ).
 */
export function ListingMap({
  items,
  center,
  onSearchHere,
}: {
  items: ListingItem[];
  center: { lat: number; lng: number };
  onSearchHere: (c: { lat: number; lng: number }) => void;
}) {
  const [view, setView] = useState({ latitude: center.lat, longitude: center.lng, zoom: 14 });
  const [moved, setMoved] = useState(false);
  const [open, setOpen] = useState<ListingItem | null>(null);

  return (
    <div className="relative h-[70vh] overflow-hidden rounded-xl border border-border">
      <Map
        {...view}
        onMove={(e) => {
          setView(e.viewState);
          setMoved(true);
        }}
        mapStyle={MAP_STYLE_URL}
        style={{ width: "100%", height: "100%" }}
      >
        <NavigationControl position="top-right" showCompass={false} />
        {items.map((item) => (
          <Marker
            key={item.listingId}
            latitude={Number(item.location.lat)}
            longitude={Number(item.location.lng)}
            onClick={(e) => {
              e.originalEvent.stopPropagation();
              setOpen(item);
            }}
          />
        ))}
        {open ? (
          <Popup latitude={Number(open.location.lat)} longitude={Number(open.location.lng)} onClose={() => setOpen(null)} maxWidth="320px">
            <ListingCard item={open} />
          </Popup>
        ) : null}
      </Map>
      {moved ? (
        <button
          type="button"
          onClick={() => {
            setMoved(false);
            onSearchHere({ lat: view.latitude, lng: view.longitude });
          }}
          className="absolute top-3 left-1/2 inline-flex min-h-11 -translate-x-1/2 items-center gap-2 rounded-full bg-background px-4 font-medium shadow"
        >
          <Search aria-hidden className="size-4" />
          Tìm ở khu vực này
        </button>
      ) : null}
    </div>
  );
}
