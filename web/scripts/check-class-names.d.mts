export function classNames(css: string): Set<string>;
export function sharedClasses(sheets: readonly { path: string; css: string }[]): { name: string; paths: string[] }[];
