"use client";

import "maplibre-gl/dist/maplibre-gl.css";

import { LocateFixed } from "lucide-react";
import { useState } from "react";
import Map, { Marker, NavigationControl, type MarkerDragEvent } from "react-map-gl/maplibre";

import { secondaryButton } from "./form-controls";

export const MAP_STYLE_URL = process.env.NEXT_PUBLIC_MAP_STYLE_URL ?? "https://tiles.openfreemap.org/styles/liberty";
const HANOI = { lat: 21.0285, lng: 105.8542 };

export interface LatLng {
  lat: number;
  lng: number;
}

/**
 * Draggable pin (docs/06-frontend.md, Form). "Dùng vị trí của tôi" asks for Geolocation only when clicked.
 * OpenStreetMap attribution stays visible (style default).
 */
export function LocationPicker({ value, onChange, error }: { value: LatLng | null; onChange: (v: LatLng) => void; error?: string }) {
  const center = value ?? HANOI;
  const [view, setView] = useState({ latitude: center.lat, longitude: center.lng, zoom: value ? 16 : 12 });
  const [locating, setLocating] = useState(false);

  const useMine = () => {
    if (!("geolocation" in navigator)) return;
    setLocating(true);
    navigator.geolocation.getCurrentPosition(
      (p) => {
        const v = { lat: p.coords.latitude, lng: p.coords.longitude };
        onChange(v);
        setView({ latitude: v.lat, longitude: v.lng, zoom: 16 });
        setLocating(false);
      },
      () => setLocating(false),
      { enableHighAccuracy: true, timeout: 10_000 },
    );
  };

  return (
    <div className="flex flex-col gap-2">
      <p className="text-sm text-muted-foreground">Bấm vào bản đồ hoặc kéo ghim tới đúng vị trí.</p>
      <div className="h-72 overflow-hidden rounded-lg border border-border">
        <Map
          {...view}
          onMove={(e) => setView(e.viewState)}
          onClick={(e) => onChange({ lat: e.lngLat.lat, lng: e.lngLat.lng })}
          mapStyle={MAP_STYLE_URL}
          style={{ width: "100%", height: "100%" }}
        >
          <NavigationControl position="top-right" showCompass={false} />
          {value ? (
            <Marker
              latitude={value.lat}
              longitude={value.lng}
              draggable
              onDragEnd={(e: MarkerDragEvent) => onChange({ lat: e.lngLat.lat, lng: e.lngLat.lng })}
            />
          ) : null}
        </Map>
      </div>
      <button type="button" onClick={useMine} disabled={locating} className={`${secondaryButton} w-fit gap-2`}>
        <LocateFixed aria-hidden className="size-4" />
        {locating ? "Đang lấy vị trí…" : "Dùng vị trí của tôi"}
      </button>
      {value ? (
        <p className="text-xs text-muted-foreground">
          Vị trí: {value.lat.toFixed(5)}, {value.lng.toFixed(5)}
        </p>
      ) : null}
      {error ? (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      ) : null}
    </div>
  );
}
