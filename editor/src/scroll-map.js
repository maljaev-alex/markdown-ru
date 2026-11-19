// A rendered table row has one Markdown line, even when its cells wrap at
// different heights. The native view interpolates between block starts in its
// own display lines, accounting for its own wrapping and folding.
export function createScrollMap(blocks, root, origin, textLength) {
  const anchors = [{ y: 0, offset: 0 }];
  function add(y, offset) {
    const previous = anchors.at(-1);
    if (y > previous.y && offset > previous.offset) anchors.push({ y, offset });
  }
  for (const { element, offset } of blocks) {
    const box = element.getBoundingClientRect();
    if (box.width && box.height) add(box.top - origin, offset);
  }
  add(root.getBoundingClientRect().bottom - origin, textLength);

  function indexAt(key, value) {
    let low = 0, high = anchors.length;
    while (low + 1 < high) {
      const mid = Math.floor((low + high) / 2);
      if (anchors[mid][key] <= value) low = mid;
      else high = mid;
    }
    return low;
  }
  return {
    at(y) {
      const i = indexAt('y', y), here = anchors[i], next = anchors[i + 1];
      return { top: here.offset, nextTop: next?.offset ?? here.offset,
        topFraction: next ? Math.max(0, (y - here.y) / (next.y - here.y)) : 0 };
    },
    yAt(offset) {
      const i = indexAt('offset', offset), here = anchors[i], next = anchors[i + 1];
      return next ? here.y + (next.y - here.y) * (offset - here.offset) / (next.offset - here.offset) : here.y;
    }
  };
}
