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
  /** Rounded path from the source circle edge to the target circle edge. */
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
 * below the line in BFS order. Jobs without needs are roots: a workflow that
 * declares no needs at all renders every job as a parallel branch. An empty
 * job list still yields the bare start→end skeleton. Fan-out branches share
 * the source's exit point; converging branches merge at a shared point before
 * a single line enters the target. Cycles are impossible (the server rejects
 * them at parse time).
 */
export function layoutDag(jobRuns: JobRun[], runStatus: RunStatus | null): DagLayout {
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

  // Lane assignment: jobs inherit the lane of one of their needs so a branch
  // keeps its own horizontal row across the whole graph. Per-layer indexing
  // would place unrelated jobs in the same row and visually chain them (e.g.
  // an independent root sitting left of a later branch). Within a layer a
  // lane is used once; when the preferred (parent) lane is taken we prefer a
  // lane that continues no other job's chain, falling back to any free lane.
  const laneOf = new Map<string, number>();
  let prevLaneOccupants = new Map<number, string[]>(); // lane -> jobs on it in the previous layer
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

  for (const [, laneKeys] of [...byLayer.entries()].sort((a, b) => a[0] - b[0])) {
    const usedThisLayer = new Set<number>();
    const assigned = new Map<string, number>();
    for (const key of laneKeys) {
      const needs = byKey.get(key)!.needs;
      const preferred = needs.filter((n) => laneOf.has(n)).map((n) => laneOf.get(n)!);
      let lane = preferred.find((l) => !usedThisLayer.has(l)) ?? -1;
      if (lane < 0) {
        // Free lanes that no previous-layer job occupies first, so we do not
        // visually extend an unrelated chain to the right.
        const maxSoFar = Math.max(0, ...laneOf.values(), ...usedThisLayer);
        for (let l = 0; l <= maxSoFar + 1 && lane < 0; l++) {
          if (usedThisLayer.has(l)) continue;
          const occupants = prevLaneOccupants.get(l) ?? [];
          if (occupants.every((o) => needs.includes(o))) lane = l;
        }
      }
      if (lane < 0) {
        for (let l = 0; lane < 0; l++) {
          if (!usedThisLayer.has(l)) lane = l;
        }
      }
      assigned.set(key, lane);
      usedThisLayer.add(lane);
    }
    prevLaneOccupants = new Map<number, string[]>();
    for (const [key, lane] of assigned) {
      laneOf.set(key, lane);
      prevLaneOccupants.set(lane, [...(prevLaneOccupants.get(lane) ?? []), key]);
    }
  }

  const nodes: DagNode[] = [];

  const startX = MARGIN + DAG_RADIUS;
  const start: DagNode = { kind: "start", jobKey: null, jobRunId: null, status: null, layer: 0, lane: 0, cx: startX, cy: SPINE_Y };
  nodes.push(start);

  const jobNodeByKey = new Map<string, DagNode>();
  for (const [layer, laneKeys] of [...byLayer.entries()].sort((a, b) => a[0] - b[0])) {
    laneKeys.forEach((jobKey) => {
      const jobRun = byKey.get(jobKey)!;
      const lane = laneOf.get(jobKey) ?? 0;
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

  // 0 floor keeps an empty job list renderable (bare start→end skeleton).
  const maxJobLayer = Math.max(0, ...byLayer.keys());
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

  // Horizontal-only line when both endpoints share a row; otherwise a rounded
  // elbow: horizontal, quarter-turn, vertical, quarter-turn, horizontal.
  const elbowTo = (from: DagNode, toX: number, toCy: number): string => {
    const x1 = from.cx + DAG_RADIUS;
    if (Math.abs(from.cy - toCy) < 1) {
      return `M ${x1} ${from.cy} L ${toX} ${toCy}`;
    }
    const dir = toCy > from.cy ? 1 : -1;
    const corner = Math.min(16, Math.abs(toCy - from.cy) / 2, Math.max(8, Math.abs(toX - x1) / 2));
    const turnX = toX - corner;
    return [
      `M ${x1} ${from.cy}`,
      `L ${turnX - corner} ${from.cy}`,
      `Q ${turnX} ${from.cy} ${turnX} ${from.cy + dir * corner}`,
      `L ${turnX} ${toCy - dir * corner}`,
      `Q ${turnX} ${toCy} ${toX} ${toCy}`,
    ].join(" ");
  };

  // Group incoming branches per target: multiple sources converge at a shared
  // merge point first (mirroring the start fan-out), then one line enters.
  const incoming = new Map<string, DagNode[]>();
  const addIncoming = (target: DagNode, source: DagNode) => {
    const key = target.kind === "end" ? "__end" : target.jobKey!;
    incoming.set(key, [...(incoming.get(key) ?? []), source]);
  };
  for (const root of keys.filter((k) => (byKey.get(k)?.needs.length ?? 0) === 0)) {
    addIncoming(jobNodeByKey.get(root)!, start);
  }
  for (const job of jobRuns) {
    for (const need of job.needs) {
      const source = jobNodeByKey.get(need);
      const target = jobNodeByKey.get(job.jobKey);
      if (source && target) addIncoming(target, source);
    }
  }
  for (const terminal of keys.filter((key) => !keys.some((other) => byKey.get(other)!.needs.includes(key)))) {
    addIncoming(end, jobNodeByKey.get(terminal)!);
  }
  if (jobRuns.length === 0) addIncoming(end, start);

  const edges: DagEdge[] = [];
  for (const [targetKey, sources] of incoming) {
    const to = targetKey === "__end" ? end : jobNodeByKey.get(targetKey)!;
    if (sources.length === 1) {
      // Single source: enter the target directly (shared exit point of start).
      edges.push({
        from: sources[0].jobKey ?? "__start",
        to: targetKey,
        d: elbowTo(sources[0], to.cx - DAG_RADIUS, to.cy),
      });
      continue;
    }
    const maxFromX = Math.max(...sources.map((s) => s.cx));
    const mergeX = (maxFromX + DAG_RADIUS + to.cx - DAG_RADIUS) / 2;
    for (const source of sources) {
      edges.push({
        from: source.jobKey ?? "__start",
        to: targetKey,
        d: elbowTo(source, mergeX, to.cy),
      });
    }
    // One shared segment from the merge point into the target.
    edges.push({
      from: targetKey,
      to: targetKey,
      d: `M ${mergeX} ${to.cy} L ${to.cx - DAG_RADIUS} ${to.cy}`,
    });
  }

  const maxLane = Math.max(0, ...nodes.filter((n) => n.kind === "job").map((n) => n.lane));
  return {
    nodes,
    edges,
    width: end.cx + DAG_RADIUS + MARGIN,
    height: SPINE_Y + maxLane * LANE_GAP + DAG_RADIUS + MARGIN,
  };
}
