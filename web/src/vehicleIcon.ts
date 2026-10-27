// Map icons as signed distance fields, so MapLibre can colour each feature from one image (icon-color) and
// outline it (icon-halo). Drawn on a canvas at startup; nothing to ship.

export const ARROW_ICON = 'vehicle-arrow'
export const STOP_ICON = 'stop-square'

/** Pixels of distance the field encodes either side of the edge; a halo can be at most about this wide. */
const RADIUS = 6

/** Boxy arrow pointing north: flat back, long body, short pointed nose. Centred, with room for the halo. */
const ARROW_W = 30
const ARROW_H = 48
export const ARROW_PATH = 'M10 42 L10 17 L15 9 L20 17 L20 42 Z'
export const ARROW_VIEWBOX = `0 0 ${ARROW_W} ${ARROW_H}`

/** Stop marker: a rounded square, like a stop sign plate. */
const STOP_SIZE = 26
export const STOP_PATH = 'M7 7 H19 V19 H7 Z'
export const STOP_VIEWBOX = `0 0 ${STOP_SIZE} ${STOP_SIZE}`

export const arrowIcon = () => sdfIcon(ARROW_W, ARROW_H, ARROW_PATH, 2)
export const stopIcon = () => sdfIcon(STOP_SIZE, STOP_SIZE, STOP_PATH, 4)

function sdfIcon(width: number, height: number, path: string, rounding: number) {
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const ctx = canvas.getContext('2d')!
  const shape = new Path2D(path)
  ctx.lineJoin = 'round'
  ctx.lineWidth = rounding
  ctx.fill(shape)
  ctx.stroke(shape)
  const mask = ctx.getImageData(0, 0, width, height).data
  const inside = (x: number, y: number) => x >= 0 && y >= 0 && x < width && y < height && mask[(y * width + x) * 4 + 3] > 127

  // Brute-force distance to the nearest pixel of the opposite kind; the icons are tiny, so this is instant.
  // MapLibre reads alpha 192 as the edge and 32 alpha steps per pixel of distance (the encoding its glyphs use).
  const data = new Uint8Array(width * height * 4)
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const me = inside(x, y)
      let best = RADIUS
      for (let dy = -RADIUS; dy <= RADIUS; dy++)
        for (let dx = -RADIUS; dx <= RADIUS; dx++)
          if (inside(x + dx, y + dy) !== me) best = Math.min(best, Math.hypot(dx, dy) - 0.5)
      const i = (y * width + x) * 4
      data[i] = data[i + 1] = data[i + 2] = 255
      data[i + 3] = Math.max(0, Math.min(255, Math.round(192 + (me ? best : -best) * 32)))
    }
  }
  return { width, height, data }
}
