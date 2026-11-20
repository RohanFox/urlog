import { useCallback, useEffect, useState } from 'react';

// The theme is the system's until the user chooses one. The choice stays in this browser. public/theme.js
// applies the same rule before the first paint, so a page never starts in the wrong colours.

export type ThemeChoice = 'system' | 'light' | 'dark';

export const THEME_KEY = 'urlog.theme';

function stored(): ThemeChoice {
  try {
    const value = window.localStorage.getItem(THEME_KEY);
    return value === 'light' || value === 'dark' ? value : 'system';
  } catch {
    return 'system';
  }
}

function systemDark(): boolean {
  return typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

export function resolve(choice: ThemeChoice): 'light' | 'dark' {
  return choice === 'system' ? (systemDark() ? 'dark' : 'light') : choice;
}

function apply(choice: ThemeChoice): void {
  document.documentElement.setAttribute('data-theme', resolve(choice));
}

export function useTheme(): { choice: ThemeChoice; setChoice: (choice: ThemeChoice) => void } {
  const [choice, setState] = useState<ThemeChoice>(stored);

  useEffect(() => {
    apply(choice);
    if (choice !== 'system' || typeof window.matchMedia !== 'function') {
      return;
    }
    // While the system decides, follow it when it changes.
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const follow = () => {
      apply('system');
    };
    media.addEventListener('change', follow);
    return () => {
      media.removeEventListener('change', follow);
    };
  }, [choice]);

  const setChoice = useCallback((next: ThemeChoice) => {
    try {
      if (next === 'system') {
        window.localStorage.removeItem(THEME_KEY);
      } else {
        window.localStorage.setItem(THEME_KEY, next);
      }
    } catch {
      // Storage refused: the choice lasts for this page.
    }
    setState(next);
  }, []);

  return { choice, setChoice };
}
