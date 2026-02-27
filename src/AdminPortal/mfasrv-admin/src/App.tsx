import { Routes, Route, NavLink } from 'react-router-dom';
import Dashboard from './pages/Dashboard';
import Users from './pages/Users';
import UserDetail from './pages/UserDetail';
import Policies from './pages/Policies';
import Sessions from './pages/Sessions';
import Agents from './pages/Agents';
import AuditLog from './pages/AuditLog';

const navItems = [
  { to: '/', label: 'Dashboard', icon: '\u25A3' },
  { to: '/users', label: 'Users', icon: '\u2691' },
  { to: '/policies', label: 'Policies', icon: '\u2696' },
  { to: '/sessions', label: 'Sessions', icon: '\u2B21' },
  { to: '/agents', label: 'Agents', icon: '\u2B23' },
  { to: '/audit', label: 'Audit Log', icon: '\u2637' },
];

export default function App() {
  return (
    <div className="app-layout">
      {/* ── Sidebar ── */}
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
      </aside>

      {/* ── Main content ── */}
      <main className="main-content">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/users" element={<Users />} />
          <Route path="/users/:id" element={<UserDetail />} />
          <Route path="/policies" element={<Policies />} />
          <Route path="/sessions" element={<Sessions />} />
          <Route path="/agents" element={<Agents />} />
          <Route path="/audit" element={<AuditLog />} />
        </Routes>
      </main>
    </div>
  );
}
