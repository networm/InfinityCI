import type { JobRun, RunStatus } from "./types";

export type DagNodeKind = "start" | "job" | "end";

export interface DagNode {
  /** "start"/"end" are virtual nodes; job nodes carry the job key. */
  kind: DagNodeKind;
  jobKey: string | null;
  jobRunId: number | null;
  status: JobRun["status"] | null;
  /** Layer = longest path from the start (left-to-right column index). */
  layer: number;
  /** Vertical row: 0 = the main line, 1..n = parallel branches hanging below. */
  lane: number;
  /** Circle center coordinates. */
  cx: number;
  cy: number;
}

export interface DagEdge {
  from: string; // jobKey or "__start"
  to: string; // jobKey or "__end"
  /** Rounded elbow path from the source circle edge to the target circle edge. */
  d: string;
}

export interface DagLayout {
  nodes: DagNode[];
  edges: DagEdge[];
  width: number;
  height: number;
}

export const DAG_RADIUS = 16;
const LAYER_GAP = 132; // horizontal distance between circle centers
const LANE_GAP = 88; // vertical distance between branch rows
const MARGIN = 34;
const SPINE_Y = MARGIN + DAG_RADIUS;

/**
 * Jenkins Blue Ocean-style layered layout: start, the primary job of each
 * layer and the end node sit on one horizontal line; parallel siblings hang
 * below the line in BFS order. Edges are rounded elbows. Cycles are
 * impossible (the server rejects them at parse time).
 */
export function layoutDag(jobRuns: JobRun[], runStatus: RunStatus | null): DagLayout | null {
  if (jobRuns.length === 0) return null;
  const hasNeeds = jobRuns.some((j) => j.needs.length > 0);
  if (!hasNeeds) return null;

  const byKey = new Map(jobRuns.map((j) => [j.jobKey, j]));
  const keys = jobRuns.map((j) => j.jobKey);

  // Longest-path layering over needs edges.
  const layerOf = new Map<string, number>();
  const computeLayer = (key: string): number => {
    const known = layerOf.get(key);
    if (known !== undefined) return known;
    const job = byKey.get(key)!;
    if (job.needs.length === 0) {
      layerOf.set(key, 0);
      return 0;
    }
    const depth = 1 + Math.max(...job.needs.map((n) => computeLayer(n)));
    layerOf.set(key, depth);
    return depth;
  };
  keys.forEach(computeLayer);

  // BFS from the roots orders jobs within each layer; the first job of a layer
  // rides the main line, its parallel siblings queue below it.
  const byLayer = new Map<number, string[]>();
  const visited = new Set<string>();
  const queue = keys.filter((k) => (byKey.get(k)?.needs.length ?? 0) === 0);
  for (const k of queue) visited.add(k);
  while (queue.length > 0) {
    const key = queue.shift()!;
    const layer = layerOf.get(key)!;
    (byLayer.get(layer) ?? byLayer.set(layer, []).get(layer)!).push(key);
    for (const dependent of keys) {
      const job = byKey.get(dependent)!;
      if (!visited.has(dependent) && job.needs.includes(key)) {
        visited.add(dependent);
        queue.push(dependent);
      }
    }
  }
  for (const key of keys) {
    if (!visited.has(key)) {
      const layer = layerOf.get(key)!;
      (byLayer.get(layer) ?? byLayer.set(layer, []).get(layer)!).push(key);
    }
  }

  const nodes: DagNode[] = [];

  // Start on the main line.
  const startX = MARGIN + DAG_RADIUS;
  const start: DagNode = { kind: "start", jobKey: null, jobRunId: null, status: null, layer: 0, lane: 0, cx: startX, cy: SPINE_Y };
  nodes.push(start);

  // Job nodes: layer + 1 (room for start), lane 0 for the first job per layer.
  const jobNodeByKey = new Map<string, DagNode>();
  for (const [layer, laneKeys] of [...byLayer.entries()].sort((a, b) => a[0] - b[0])) {
    laneKeys.forEach((jobKey, lane) => {
      const jobRun = byKey.get(jobKey)!;
      const node: DagNode = {
        kind: "job",
        jobKey,
        jobRunId: jobRun.id,
        status: jobRun.status,
        layer: layer + 1,
        lane,
        cx: startX + (layer + 1) * LAYER_GAP,
        cy: SPINE_Y + lane * LANE_GAP,
      };
      jobNodeByKey.set(jobKey, node);
      nodes.push(node);
    });
  }

  // End back on the main line, one column past the deepest job.
  const maxJobLayer = Math.max(...byLayer.keys());
  const end: DagNode = {
    kind: "end",
    jobKey: null,
    jobRunId: null,
    status: runStatus,
    layer: maxJobLayer + 2,
    lane: 0,
    cx: startX + (maxJobLayer + 2) * LAYER_GAP,
    cy: SPINE_Y,
  };
  nodes.push(end);

  // Rounded elbow: horizontal from source, quarter-turn, vertical, quarter-turn,
  // horizontal into the target.
  const elbow = (from: DagNode, to: DagNode): string => {
    const x1 = from.cx + DAG_RADIUS;
    const x2 = to.cx - DAG_RADIUS;
    if (Math.abs(from.cy - to.cy) < 1) {
      return `M ${x1} ${from.cy} L ${x2} ${to.cy}`;
    }
    const midX = (x1 + x2) / 2;
    const dir = to.cy > from.cy ? 1 : -1;
    const corner = Math.min(16, Math.abs(to.cy - from.cy) / 2);
    return [
      `M ${x1} ${from.cy}`,
      `L ${midX - corner} ${from.cy}`,
      `Q ${midX} ${from.cy} ${midX} ${from.cy + dir * corner}`,
      `L ${midX} ${to.cy - dir * corner}`,
      `Q ${midX} ${to.cy} ${midX + corner} ${to.cy}`,
      `L ${x2} ${to.cy}`,
    ].join(" ");
  };

  const edges: DagEdge[] = [];
  for (const root of keys.filter((k) => (byKey.get(k)?.needs.length ?? 0) === 0)) {
    const target = jobNodeByKey.get(root)!;
    edges.push({ from: "__start", to: root, d: elbow(start, target) });
  }
  for (const job of jobRuns) {
    for (const need of job.needs) {
      const from = jobNodeByKey.get(need);
      const to = jobNodeByKey.get(job.jobKey);
      if (from && to) edges.push({ from: need, to: job.jobKey, d: elbow(from, to) });
    }
  }
  for (const terminal of keys.filter((key) => !keys.some((other) => byKey.get(other)!.needs.includes(key)))) {
    const from = jobNodeByKey.get(terminal)!;
    edges.push({ from: terminal, to: "__end", d: elbow(from, end) });
  }

  const maxLane = Math.max(0, ...nodes.filter((n) => n.kind === "job").map((n) => n.lane));
  return {
    nodes,
    edges,
    width: end.cx + DAG_RADIUS + MARGIN,
    height: SPINE_Y + maxLane * LANE_GAP + DAG_RADIUS + MARGIN,
  };
}
