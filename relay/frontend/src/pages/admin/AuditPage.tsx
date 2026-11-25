import { ErrorNote } from "../../components/Bits";
import { dateTime } from "../../format";
import { useResource } from "../../hooks";

type Entry = { at: string; actor: string | null; action: string; targetType: string | null; targetName: string | null };

export function AuditPage() {
  const { data, error } = useResource<Entry[]>("/mgmt/admin/audit?limit=300", 15000);
  return (
    <div className="space-y-4">
      <h1 className="h1">Audit log</h1>
      <p className="text-sm text-slate-400">Admin actions, logins and key and token lifecycle. Failed logins go to the relay's stdout with source IP.</p>
      <ErrorNote error={error} />
      <div className="card overflow-x-auto">
        <table className="table">
          <thead>
            <tr>
              <th>When</th>
              <th>Actor</th>
              <th>Action</th>
              <th>Target</th>
            </tr>
          </thead>
          <tbody>
            {data?.map((e, i) => (
              <tr key={i}>
                <td className="whitespace-nowrap">{dateTime(e.at)}</td>
                <td>{e.actor ?? "system"}</td>
                <td className="font-mono text-xs text-amber-300">{e.action}</td>
                <td className="text-slate-300">
                  {e.targetType && <span className="text-slate-500">{e.targetType}: </span>}
                  {e.targetName}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
