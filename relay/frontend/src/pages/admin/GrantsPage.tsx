import { useEffect, useRef, useState } from "react";
import { api, type AdminUser, type Publication } from "../../api";
import { Empty, ErrorNote } from "../../components/Bits";
import { useResource } from "../../hooks";

type Grant = { userId: string; publicationId: string };

function TriCheckbox({ state, onChange, title }: { state: "all" | "some" | "none"; onChange: (v: boolean) => void; title: string }) {
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = state === "some";
  }, [state]);
  return (
    <input
      ref={ref}
      type="checkbox"
      title={title}
      className="h-4 w-4 accent-amber-500"
      checked={state === "all"}
      onChange={(e) => onChange(state === "some" ? true : e.target.checked)}
    />
  );
}

/** Users × aliases. One click grants an alias on every node that publishes it; expand an alias for per-node control. */
export function GrantsPage() {
  const users = useResource<AdminUser[]>("/mgmt/admin/users");
  const pubs = useResource<Publication[]>("/mgmt/admin/publications");
  const grants = useResource<Grant[]>("/mgmt/admin/grants");
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);

  if (!users.data || !pubs.data || !grants.data) return <ErrorNote error={users.error ?? pubs.error ?? grants.error} />;

  const set = new Set(grants.data.map((g) => `${g.userId}|${g.publicationId}`));
  const aliases = [...new Set(pubs.data.map((p) => p.alias))].sort();
  const byAlias = (a: string) => pubs.data!.filter((p) => p.alias === a);

  const put = async (body: object) => {
    setError(null);
    try {
      await api.put("/mgmt/admin/grants", body);
      grants.reload();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <div className="space-y-4">
      <h1 className="h1">Grants</h1>
      <p className="text-sm text-slate-400">
        A grant lets a user use a model on a node. Ticking an alias grants it on every node in its pool. A half-filled box means some
        nodes only; click the alias name to see them.
      </p>
      <ErrorNote error={error} />
      {aliases.length === 0 && <Empty>Nothing is published yet.</Empty>}
      {aliases.length > 0 && (
        <div className="card overflow-x-auto">
          <table className="table">
            <thead>
              <tr>
                <th>User</th>
                {aliases.map((a) => (
                  <th key={a} className="text-center normal-case">
                    <button className="font-mono text-amber-300 hover:underline" onClick={() => setExpanded(expanded === a ? null : a)}>
                      {a}
                    </button>
                    <div className="text-[10px] text-slate-500">{byAlias(a).length} node(s)</div>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {users.data.map((u) => (
                <tr key={u.id}>
                  <td className="font-mono">
                    {u.username}
                    {u.disabled && <span className="ml-1 text-xs text-red-400">disabled</span>}
                  </td>
                  {aliases.map((a) => {
                    const ps = byAlias(a);
                    const n = ps.filter((p) => set.has(`${u.id}|${p.id}`)).length;
                    const state = n === 0 ? "none" : n === ps.length ? "all" : "some";
                    return (
                      <td key={a} className="text-center">
                        <TriCheckbox
                          state={state}
                          title={`${u.username} → ${a}`}
                          onChange={(granted) => put({ userId: u.id, alias: a, granted })}
                        />
                        {expanded === a && (
                          <div className="mt-1 space-y-0.5 text-left text-xs">
                            {ps.map((p) => (
                              <label key={p.id} className="flex items-center gap-1 whitespace-nowrap text-slate-400">
                                <input
                                  type="checkbox"
                                  className="accent-amber-500"
                                  checked={set.has(`${u.id}|${p.id}`)}
                                  onChange={(e) => put({ userId: u.id, publicationId: p.id, granted: e.target.checked })}
                                />
                                {p.node}
                              </label>
                            ))}
                          </div>
                        )}
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
