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
  /** Vertical lane within the layer (top-to-bottom row index). */
  lane: number;
  /** Circle center coordinates. */
  cx: number;
  cy: number;
}

export interface DagEdge {
  from: string; // jobKey or "__start"
  to: string; // jobKey or "__end"
  /** Elbow polyline from source circle edge to target circle edge. */
  points: string;
}

export interface DagLayout {
  nodes: DagNode[];
  edges: DagEdge[];
  width: number;
  height: number;
}

export const DAG_RADIUS = 14;
const LAYER_GAP = 64; // between circle centers
const LANE_GAP = 52; // room for the label under each circle
const MARGIN = 24;

/**
 * Jenkins-style layered DAG (left → right): circular job nodes with labels
 * underneath, plus virtual start/end nodes. Layers are the longest-path depth;
 * within a layer nodes are ordered BFS from the roots to reduce crossings.
 * Cycles are impossible (the server rejects them at parse time).
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

  // BFS lane assignment from roots.
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

  // Job node layers shift by 1 to make room for the start node; end gets max+2.
  const maxJobLayer = Math.max(...byLayer.keys());
  const nodes: DagNode[] = [];
  const push = (node: DagNode) => nodes.push(node);

  const startX = MARGIN + DAG_RADIUS;
  push({ kind: "start", jobKey: null, jobRunId: null, status: null, layer: 0, lane: 0, cx: startX, cy: MARGIN + DAG_RADIUS });

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
        cy: MARGIN + DAG_RADIUS + lane * LANE_GAP,
      };
      jobNodeByKey.set(jobKey, node);
      push(node);
    });
  }

  const endLayer = maxJobLayer + 2;
  const endLaneCenter = MARGIN + DAG_RADIUS + (Math.max(...nodes.filter((n) => n.kind === "job").map((n) => n.lane)) / 2) * LANE_GAP;
  const endNode: DagNode = {
    kind: "end",
    jobKey: null,
    jobRunId: null,
    status: runStatus,
    layer: endLayer,
    lane: 0,
    cx: startX + endLayer * LAYER_GAP,
    cy: endLaneCenter,
  };
  push(endNode);

  // Edges: start → roots, needs edges, terminals → end.
  const edges: DagEdge[] = [];
  const terminals = keys.filter((key) => !keys.some((other) => byKey.get(other)!.needs.includes(key)));
  const elbow = (from: DagNode, to: DagNode): string => {
    const x1 = from.cx + DAG_RADIUS;
    const x2 = to.cx - DAG_RADIUS;
    const midX = (x1 + x2) / 2;
    return `${x1},${from.cy} ${midX},${from.cy} ${midX},${to.cy} ${x2},${to.cy}`;
  };

  for (const root of keys.filter((k) => (byKey.get(k)?.needs.length ?? 0) === 0)) {
    const target = jobNodeByKey.get(root)!;
    edges.push({ from: "__start", to: root, points: elbow(nodes[0], target) });
  }
  for (const job of jobRuns) {
    for (const need of job.needs) {
      const from = jobNodeByKey.get(need);
      const to = jobNodeByKey.get(job.jobKey);
      if (from && to) edges.push({ from: need, to: job.jobKey, points: elbow(from, to) });
    }
  }
  for (const terminal of terminals) {
    const from = jobNodeByKey.get(terminal)!;
    edges.push({ from: terminal, to: "__end", points: elbow(from, endNode) });
  }

  const width = endNode.cx + DAG_RADIUS + MARGIN;
  const height = MARGIN * 2 + DAG_RADIUS * 2 + Math.max(0, Math.max(...nodes.filter((n) => n.kind === "job").map((n) => n.lane))) * LANE_GAP;
  return { nodes, edges, width, height };
}
