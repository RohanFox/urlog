import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Icon, type IconName } from '../ui/Icon';
import { THEME_KEY } from './theme';
import { ThemeButton } from './ThemeButton';

/** A stand-in for the system's colour setting that can change while the page is open. */
function systemSetting(dark: boolean) {
  const listeners = new Set<() => void>();
  const media = {
    get matches() {
      return dark;
    },
    addEventListener: (_: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_: string, listener: () => void) => listeners.delete(listener),
  };
  vi.stubGlobal('matchMedia', () => media);
  return {
    change(next: boolean) {
      dark = next;
      listeners.forEach((listener) => {
        listener();
      });
    },
    listening: () => listeners.size,
  };
}

/** The geometry an element draws: its one path. */
function shape(element: Element): string | null | undefined {
  return element.querySelector('path')?.getAttribute('d');
}

function drawn(name: IconName): string | null | undefined {
  const { container, unmount } = render(<Icon name={name} />);
  const path = shape(container);
  unmount();
  return path;
}

const theme = () => document.documentElement.getAttribute('data-theme');

beforeEach(() => {
  document.documentElement.removeAttribute('data-theme');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the theme button', () => {
  it('follows the system on a first visit and stores nothing', () => {
    systemSetting(true);
    render(<ThemeButton />);
    expect(theme()).toBe('dark');
    expect(screen.getByRole('button', { name: 'Switch to the light theme' })).toHaveAttribute('title', 'Switch to the light theme');
    expect(window.localStorage.getItem(THEME_KEY)).toBeNull();
  });

  it('shows a sun in dark and a moon in light', async () => {
    const sun = drawn('sun');
    const moon = drawn('moon');
    expect(sun).toBeTruthy();
    expect(sun).not.toBe(moon);
    systemSetting(true);
    const user = userEvent.setup();
    render(<ThemeButton />);
    const button = screen.getByRole('button');
    expect(shape(button)).toBe(sun);
    await user.click(button);
    expect(shape(button)).toBe(moon);
    expect(button).not.toHaveAttribute('aria-pressed');
  });

  it('switches on a click and stores the choice under the key of today', async () => {
    systemSetting(false);
    const user = userEvent.setup();
    render(<ThemeButton />);
    expect(theme()).toBe('light');

    await user.click(screen.getByRole('button', { name: 'Switch to the dark theme' }));
    expect(theme()).toBe('dark');
    expect(window.localStorage.getItem(THEME_KEY)).toBe('dark');

    await user.click(screen.getByRole('button', { name: 'Switch to the light theme' }));
    expect(theme()).toBe('light');
    expect(window.localStorage.getItem(THEME_KEY)).toBe('light');
  });

  it('takes Enter and Space', async () => {
    systemSetting(false);
    const user = userEvent.setup();
    render(<ThemeButton />);
    await user.tab();
    expect(screen.getByRole('button')).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(theme()).toBe('dark');
    await user.keyboard(' ');
    expect(theme()).toBe('light');
  });

  it('starts from a stored choice whatever the system says', () => {
    systemSetting(true);
    window.localStorage.setItem(THEME_KEY, 'light');
    render(<ThemeButton />);
    expect(theme()).toBe('light');
    expect(screen.getByRole('button', { name: 'Switch to the dark theme' })).toBeInTheDocument();
  });

  it('takes no stored value it does not know as a choice', () => {
    systemSetting(true);
    window.localStorage.setItem(THEME_KEY, 'system');
    render(<ThemeButton />);
    expect(theme()).toBe('dark');
  });

  it('follows a change of the system while nothing is stored, and stops once the user chooses', async () => {
    const setting = systemSetting(false);
    const user = userEvent.setup();
    render(<ThemeButton />);
    expect(theme()).toBe('light');

    act(() => {
      setting.change(true);
    });
    expect(await screen.findByRole('button', { name: 'Switch to the light theme' })).toBeInTheDocument();
    expect(theme()).toBe('dark');

    await user.click(screen.getByRole('button'));
    expect(theme()).toBe('light');
    expect(setting.listening()).toBe(0);
    act(() => {
      setting.change(false);
      setting.change(true);
    });
    expect(theme()).toBe('light');
  });

  it('switches for the page when the browser refuses to store', async () => {
    systemSetting(false);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('refused');
    });
    const user = userEvent.setup();
    render(<ThemeButton />);
    await user.click(screen.getByRole('button'));
    expect(theme()).toBe('dark');
  });
});
