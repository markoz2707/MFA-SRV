import { Routes, Route, NavLink } from 'react-router-dom';
import {
  LayoutDashboard,
  Users as UsersIcon,
  Shield,
  Key,
  Server,
  FileText,
  Database,
  Settings,
} from 'lucide-react';
import Dashboard from './pages/Dashboard';
import UsersPage from './pages/Users';
import UserDetail from './pages/UserDetail';
import Policies from './pages/Policies';
import Sessions from './pages/Sessions';
import Agents from './pages/Agents';
import AuditLog from './pages/AuditLog';
import Backups from './pages/Backups';
import SettingsPage from './pages/Settings';
import ToastContainer from './components/Toast';
import { useToast } from './hooks/useToast';
import { createContext, useContext } from 'react';
import type { ToastItem } from './hooks/useToast';

interface ToastContextValue {
  toasts: ToastItem[];
  showSuccess: (msg: string) => void;
  showError: (msg: string) => void;
  dismiss: (id: number) => void;
}

export const ToastContext = createContext<ToastContextValue>({
  toasts: [],
  showSuccess: () => {},
  showError: () => {},
  dismiss: () => {},
});

export function useAppToast() {
  return useContext(ToastContext);
}

const navItems = [
  { to: '/', label: 'Dashboard', icon: <LayoutDashboard size={18} /> },
  { to: '/users', label: 'Users', icon: <UsersIcon size={18} /> },
  { to: '/policies', label: 'Policies', icon: <Shield size={18} /> },
  { to: '/sessions', label: 'Sessions', icon: <Key size={18} /> },
  { to: '/agents', label: 'Agents', icon: <Server size={18} /> },
  { to: '/audit', label: 'Audit Log', icon: <FileText size={18} /> },
  { to: '/backups', label: 'Backups', icon: <Database size={18} /> },
  { to: '/settings', label: 'Settings', icon: <Settings size={18} /> },
];

export default function App() {
  const toast = useToast();

  return (
    <ToastContext.Provider value={toast}>
      <div className="app-layout">
        {/* Sidebar */}
        <aside className="sidebar">
          <div className="sidebar-brand">
            <div className="sidebar-brand-icon">MFA</div>
            <div>
              <h1>MfaSrv</h1>
              <span>Admin Portal</span>
            </div>
          </div>

          <ul className="sidebar-nav">
            {navItems.map((item) => (
              <li key={item.to}>
                <NavLink
                  to={item.to}
                  end={item.to === '/'}
                  className={({ isActive }) => (isActive ? 'active' : '')}
                >
                  <span className="nav-icon">{item.icon}</span>
                  <span>{item.label}</span>
                </NavLink>
              </li>
            ))}
          </ul>

          <div className="sidebar-version">MfaSrv v1.0.0</div>
        </aside>

        {/* Main content */}
        <main className="main-content">
          <Routes>
            <Route path="/" element={<Dashboard />} />
            <Route path="/users" element={<UsersPage />} />
            <Route path="/users/:id" element={<UserDetail />} />
            <Route path="/policies" element={<Policies />} />
            <Route path="/sessions" element={<Sessions />} />
            <Route path="/agents" element={<Agents />} />
            <Route path="/audit" element={<AuditLog />} />
            <Route path="/backups" element={<Backups />} />
            <Route path="/settings" element={<SettingsPage />} />
          </Routes>
        </main>

        <ToastContainer toasts={toast.toasts} dismiss={toast.dismiss} />
      </div>
    </ToastContext.Provider>
  );
}
