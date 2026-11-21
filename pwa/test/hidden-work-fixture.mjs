import { readFile } from 'node:fs/promises';

export function indicatorStyle(el, pseudo) {
  const style = getComputedStyle(el, pseudo);
  return { animationName: style.animationName, animationDuration: style.animationDuration,
    animationIterationCount: style.animationIterationCount, animationPlayState: style.animationPlayState,
    animationNames: el.getAnimations({ subtree: true }).map((animation) => animation.animationName) };
}

export function readFont(path) {
  const name = path.split('/').at(-1);
  const family = name.startsWith('SlipgatesMono-') ? 'slipgates-mono' : 'slipgates-sans';
  return readFile(new URL(`../../design/fonts/${family}/woff2/${name}`, import.meta.url));
}
