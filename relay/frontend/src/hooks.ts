import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "./api";

/** Loads a resource and optionally re-polls it. Live views simply poll every few seconds. */
export function useResource<T>(path: string | null, pollMs?: number) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const pathRef = useRef(path);
  pathRef.current = path;

  const reload = useCallback(async () => {
    if (!pathRef.current) return;
    try {
      setData(await api.get<T>(pathRef.current));
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }, []);

  useEffect(() => {
    reload();
    if (!pollMs) return;
    const timer = setInterval(() => {
      if (document.visibilityState === "visible") reload();
    }, pollMs);
    return () => clearInterval(timer);
  }, [path, pollMs, reload]);

  return { data, error, reload };
}
