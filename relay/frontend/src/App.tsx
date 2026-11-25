import { useEffect, useState } from "react";
import { Navigate, Route, Routes, useLocation } from "react-router";
import { refresh, setSessionListener, type User } from "./api";
import { Layout } from "./components/Layout";
import { LoginPage } from "./pages/LoginPage";
import { LinkPage } from "./pages/LinkPage";
import { ForcedPasswordPage } from "./pages/ForcedPasswordPage";
import { MyModelsPage } from "./pages/MyModelsPage";
import { KeysPage } from "./pages/KeysPage";
import { MyUsagePage } from "./pages/MyUsagePage";
import { AccountPage } from "./pages/AccountPage";
import { NodesPage } from "./pages/admin/NodesPage";
import { ModelsPage } from "./pages/admin/ModelsPage";
import { UsersPage } from "./pages/admin/UsersPage";
import { GrantsPage } from "./pages/admin/GrantsPage";
import { UsagePage } from "./pages/admin/UsagePage";
import { AuditPage } from "./pages/admin/AuditPage";

export default function App() {
  const [user, setUser] = useState<User | null>(null);
  const [ready, setReady] = useState(false);
  const location = useLocation();

  useEffect(() => {
    setSessionListener(setUser);
    refresh().finally(() => setReady(true));
  }, []);

  // Invite and reset links work signed in or not.
  if (location.pathname === "/invite" || location.pathname === "/reset") {
    return <LinkPage kind={location.pathname === "/invite" ? "invite" : "reset"} />;
  }

  if (!ready) {
    return <div className="p-8 font-mono text-slate-500">connecting…</div>;
  }

  if (!user) {
    return <LoginPage />;
  }

  if (user.mustChangePassword) {
    return <ForcedPasswordPage user={user} />;
  }

  return (
    <Layout user={user}>
      <Routes>
        <Route path="/" element={<Navigate to="/models" replace />} />
        <Route path="/models" element={<MyModelsPage />} />
        <Route path="/keys" element={<KeysPage />} />
        <Route path="/usage" element={<MyUsagePage />} />
        <Route path="/account" element={<AccountPage user={user} />} />
        {user.isAdmin && (
          <>
            <Route path="/admin/nodes" element={<NodesPage />} />
            <Route path="/admin/models" element={<ModelsPage />} />
            <Route path="/admin/users" element={<UsersPage self={user} />} />
            <Route path="/admin/grants" element={<GrantsPage />} />
            <Route path="/admin/usage" element={<UsagePage />} />
            <Route path="/admin/audit" element={<AuditPage />} />
          </>
        )}
        <Route path="*" element={<Navigate to="/models" replace />} />
      </Routes>
    </Layout>
  );
}
