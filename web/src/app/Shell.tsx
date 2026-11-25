import * as Menu from '@radix-ui/react-dropdown-menu';
import { useEffect, useState, type ReactNode } from 'react';
import { Link, NavLink } from 'react-router';
import { Lockup } from '../brand/Logo';
import { Banner } from '../ui/controls';
import { Icon } from '../ui/Icon';
import { useSession, useUser } from './session';
import { useTheme, type ThemeChoice } from './theme';
import { uploads } from '../upload/queue';
import './shell.css';

const THEMES: ReadonlyArray<{ key: ThemeChoice; label: string }> = [
  { key: 'system', label: 'As the system' },
  { key: 'light', label: 'Light' },
  { key: 'dark', label: 'Dark' },
];

function UserMenu({ onSignOutFailed }: { onSignOutFailed: () => void }) {
  const user = useUser();
  const { signOut } = useSession();
  const { choice, setChoice } = useTheme();
  return (
    <Menu.Root>
      <Menu.Trigger className="user-button" aria-label={`Signed in as ${user.name}. Account and theme`}>
        <span className="avatar" aria-hidden="true">
          {user.name.slice(0, 2).toUpperCase()}
        </span>
        <span className="user-name mono">{user.name}</span>
        <Icon name="down" />
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Content className="menu" align="end" sideOffset={4}>
          <Menu.Label className="menu-label">
            {user.name} · {user.role}
          </Menu.Label>
          <Menu.Separator className="menu-line" />
          <Menu.Label className="menu-label">Theme</Menu.Label>
          <Menu.RadioGroup
            value={choice}
            onValueChange={(value) => {
              setChoice(value as ThemeChoice);
            }}
          >
            {THEMES.map((t) => (
              <Menu.RadioItem key={t.key} value={t.key} className="menu-item">
                <span className="menu-check">
                  <Menu.ItemIndicator>
                    <Icon name="check" />
                  </Menu.ItemIndicator>
                </span>
                {t.label}
              </Menu.RadioItem>
            ))}
          </Menu.RadioGroup>
          <Menu.Separator className="menu-line" />
          <Menu.Item
            className="menu-item"
            onSelect={() => {
              signOut().catch(onSignOutFailed);
            }}
          >
            <span className="menu-check">
              <Icon name="out" />
            </span>
            Sign out
          </Menu.Item>
        </Menu.Content>
      </Menu.Portal>
    </Menu.Root>
  );
}

/** The frame of every page behind the session: the header with the logo, the places and the user. */
export function Shell({ children }: { children: ReactNode }) {
  const [signOutFailed, setSignOutFailed] = useState(false);

  // Closing or reloading the tab while bytes have still to reach the server asks first. The browser words the
  // question itself; a page may only say that it has one.
  useEffect(() => {
    const ask = (event: BeforeUnloadEvent) => {
      if (uploads.unsafeToLeave()) {
        event.preventDefault();
      }
    };
    window.addEventListener('beforeunload', ask);
    return () => {
      window.removeEventListener('beforeunload', ask);
    };
  }, []);
  return (
    <div className="app">
      <header className="header">
        <Link className="header-brand" to="/cases" aria-label="urlog, all cases">
          <Lockup label="" />
        </Link>
        <nav className="tabs" aria-label="Main">
          <NavLink className="tab" to="/cases">
            <Icon name="folder" />
            Cases
          </NavLink>
        </nav>
        <span className="grow" />
        <UserMenu
          onSignOutFailed={() => {
            setSignOutFailed(true);
          }}
        />
      </header>
      {signOutFailed ? (
        <div className="shell-notice">
          <Banner kind="error" title="You are still signed in">
            The server did not answer the sign-out. Try again, or close the browser to end the session on this machine.
          </Banner>
        </div>
      ) : null}
      {children}
    </div>
  );
}
