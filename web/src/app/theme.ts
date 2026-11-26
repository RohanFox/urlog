import { useCallback, useEffect, useState } from 'react';

// The theme is the system's until the user chooses one. The choice stays in this browser, and nothing in the page
// leads back to the system's setting; clearing the site's data does. public/theme.js applies the same rule before
// the first paint, so a page never starts in the wrong colours.

export type Theme = 'light' | 'dark';

export const THEME_KEY = 'urlog.theme';

const DARK = '(prefers-color-scheme: dark)';

function stored(): Theme | undefined {
  try {
    const value = window.localStorage.getItem(THEME_KEY);
    return value === 'light' || value === 'dark' ? value : undefined;
  } catch {
    return undefined;
  }
}

function system(): Theme {
  return typeof window.matchMedia === 'function' && window.matchMedia(DARK).matches ? 'dark' : 'light';
}

/** The theme the page shows, and the press that switches to the other one and keeps it. */
export function useTheme(): { theme: Theme; toggle: () => void } {
  const [choice, setChoice] = useState<Theme | undefined>(stored);
  const [followed, setFollowed] = useState<Theme>(system);
  const theme = choice ?? followed;

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
  }, [theme]);

  useEffect(() => {
    if (choice !== undefined || typeof window.matchMedia !== 'function') {
      return;
    }
    // While nothing is stored, follow the system when it changes.
    const media = window.matchMedia(DARK);
    const follow = () => {
      setFollowed(media.matches ? 'dark' : 'light');
    };
    follow();
    media.addEventListener('change', follow);
    return () => {
      media.removeEventListener('change', follow);
    };
  }, [choice]);

  const toggle = useCallback(() => {
    const next: Theme = theme === 'dark' ? 'light' : 'dark';
    try {
      window.localStorage.setItem(THEME_KEY, next);
    } catch {
      // Storage refused: the choice lasts for this page.
    }
    setChoice(next);
  }, [theme]);

  return { theme, toggle };
}
