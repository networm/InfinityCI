import type { JobRun } from "./types";

export interface DagNode {
  jobKey: string;
  jobRunId: number;
  status: JobRun["status"];
  /** Layer = longest path from a root; left-to-right column index. */
  layer: number;
  /** Vertical lane within the layer (top-to-bottom row index). */
  lane: number;
  x: number;
  y: number;
}

export interface DagEdge {
  from: string;
  to: string;
  /** Polyline points from source node's right edge to target node's left edge. */
  points: string;
}

export interface DagLayout {
  nodes: DagNode[];
  edges: DagEdge[];
  width: number;
  height: number;
}

const NODE_WIDTH = 132;
const NODE_HEIGHT = 40;
const LAYER_GAP = 56;
const LANE_GAP = 12;

/**
 * Layered DAG layout (left → right). Layers are the longest-path depth from
 * roots; within a layer, nodes are ordered by a DFS from roots to reduce edge
 * crossings. Cycles are impossible (the server rejects them at parse time).
 */
export function layoutDag(jobRuns: JobRun[]): DagLayout | null {
  if (jobRuns.length === 0) return null;
  const hasNeeds = jobRuns.some((j) => j.needs.length > 0);
  if (!hasNeeds) return null;

  const byKey = new Map(jobRuns.map((j) => [j.jobKey, j]));
  const keys = jobRuns.map((j) => j.jobKey);

  // Longest-path layering.
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

  // Lane assignment: group by layer, order by first-seen in BFS from roots.
  const byLayer = new Map<number, string[]>();
  const visited = new Set<string>();
  const queue = keys.filter((k) => (byKey.get(k)?.needs.length ?? 0) === 0);
  for (const k of queue) visited.add(k);
  while (queue.length > 0) {
    const key = queue.shift()!;
    const layer = layerOf.get(key)!;
    const bucket = byLayer.get(layer) ?? [];
    bucket.push(key);
    byLayer.set(layer, bucket);
    for (const dependent of keys) {
      const job = byKey.get(dependent)!;
      if (!visited.has(dependent) && job.needs.includes(key)) {
        visited.add(dependent);
        queue.push(dependent);
      }
    }
  }
  // Any node not reached (shouldn't happen without cycles) gets appended.
  for (const key of keys) {
    if (!visited.has(key)) {
      const layer = layerOf.get(key)!;
      const bucket = byLayer.get(layer) ?? [];
      bucket.push(key);
      byLayer.set(layer, bucket);
      visited.add(key);
    }
  }

  const nodes: DagNode[] = [];
  for (const [layer, laneKeys] of [...byLayer.entries()].sort((a, b) => a[0] - b[0])) {
    laneKeys.forEach((jobKey, lane) => {
      const jobRun = byKey.get(jobKey)!;
      nodes.push({
        jobKey,
        jobRunId: jobRun.id,
        status: jobRun.status,
        layer,
        lane,
        x: layer * (NODE_WIDTH + LAYER_GAP),
        y: lane * (NODE_HEIGHT + LANE_GAP),
      });
    });
  }

  const nodeByKey = new Map(nodes.map((n) => [n.jobKey, n]));
  const edges: DagEdge[] = [];
  for (const job of jobRuns) {
    for (const need of job.needs) {
      const from = nodeByKey.get(need);
      const to = nodeByKey.get(job.jobKey);
      if (!from || !to) continue;
      const x1 = from.x + NODE_WIDTH;
      const y1 = from.y + NODE_HEIGHT / 2;
      const x2 = to.x;
      const y2 = to.y + NODE_HEIGHT / 2;
      const midX = x1 + (x2 - x1) / 2;
      edges.push({
        from: need,
        to: job.jobKey,
        points: `${x1},${y1} ${midX},${y1} ${midX},${y2} ${x2},${y2}`,
      });
    }
  }

  const maxLane = Math.max(...nodes.map((n) => n.lane));
  const maxLayer = Math.max(...nodes.map((n) => n.layer));
  return {
    nodes,
    edges,
    width: (maxLayer + 1) * NODE_WIDTH + maxLayer * LAYER_GAP,
    height: (maxLane + 1) * NODE_HEIGHT + maxLane * LANE_GAP,
  };
}

export const DAG_NODE_WIDTH = NODE_WIDTH;
export const DAG_NODE_HEIGHT = NODE_HEIGHT;
