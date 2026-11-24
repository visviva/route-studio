const exactLimit = 14;
const epsilon = 1e-9;

self.onmessage = ({ data: { coordinates, loop, aspect } }) => {
    self.postMessage(optimizeRoute(coordinates, loop, aspect));
};

export function optimizeRoute(coordinates, loop, aspect = 1) {
    const points = coordinates.map(([x, y]) => [x * aspect, y]);
    const initial = points.map((_, index) => index);
    if (points.length < 3) return initial;

    function squaredDistance(a, b) {
        const dx = points[a][0] - points[b][0];
        const dy = points[a][1] - points[b][1];
        return dx * dx + dy * dy;
    }

    const distance = (a, b) => Math.sqrt(squaredDistance(a, b));
    const candidate = points.length <= exactLimit
        ? heldKarp(points.length, loop, distance)
        : approximate(points, initial, loop, squaredDistance, distance);

    if (length(candidate, loop, distance) >= length(initial, loop, distance) - epsilon) return initial;
    if (!loop && candidate.at(-1) < candidate[0]) candidate.reverse();
    return candidate;
}

function length(order, loop, distance) {
    let total = loop ? distance(order.at(-1), order[0]) : 0;
    for (let i = 1; i < order.length; i++) total += distance(order[i - 1], order[i]);
    return total;
}

function heldKarp(count, loop, distance) {
    const full = (1 << count) - 1;
    const costs = new Float64Array((full + 1) * count).fill(Infinity);
    const previous = new Int8Array(costs.length).fill(-1);
    const distances = Array.from({ length: count }, (_, from) =>
        Float64Array.from({ length: count }, (_, to) => distance(from, to)));

    // A loop starts at waypoint 0. An open path may start at any waypoint.
    for (let start = 0; start < (loop ? 1 : count); start++) costs[(1 << start) * count + start] = 0;

    for (let visited = 1; visited < full; visited++) {
        for (let end = 0; end < count; end++) {
            const cost = costs[visited * count + end];
            if (cost === Infinity) continue;

            for (let remaining = full ^ visited; remaining; remaining &= remaining - 1) {
                const bit = remaining & -remaining;
                const next = 31 - Math.clz32(bit);
                const index = (visited | bit) * count + next;
                const nextCost = cost + distances[end][next];
                if (nextCost < costs[index]) {
                    costs[index] = nextCost;
                    previous[index] = end;
                }
            }
        }
    }

    let best = Infinity;
    let end = -1;
    for (let point = 0; point < count; point++) {
        const cost = costs[full * count + point] + (loop ? distances[point][0] : 0);
        if (cost < best) {
            best = cost;
            end = point;
        }
    }

    const order = new Array(count);
    for (let index = count - 1, visited = full; index >= 0; index--) {
        order[index] = end;
        const next = previous[visited * count + end];
        visited ^= 1 << end;
        end = next;
    }
    return order;
}

function approximate(points, initial, loop, squaredDistance, distance) {
    let best = initial.slice();
    let bestLength = length(best, loop, distance);
    const starts = new Set([0]);
    if (points.length <= 1000) {
        // Add seeds near the map's extremes to reduce dependence on the initial start.
        for (const axis of [0, 1]) {
            let min = 0;
            let max = 0;
            for (let point = 1; point < points.length; point++) {
                if (points[point][axis] < points[min][axis]) min = point;
                if (points[point][axis] > points[max][axis]) max = point;
            }
            starts.add(min);
            starts.add(max);
        }
    }

    for (const start of starts) {
        const candidate = nearestNeighbor(points.length, start, squaredDistance);
        const candidateLength = length(candidate, loop, distance);
        if (candidateLength < bestLength - epsilon) {
            best = candidate;
            bestLength = candidateLength;
        }
    }

    if (loop) {
        const first = best.indexOf(0);
        best = best.slice(first).concat(best.slice(0, first));
    }
    improveWithTwoOpt(best, loop, distance);
    return best;
}

function nearestNeighbor(count, start, squaredDistance) {
    const visited = new Uint8Array(count);
    const order = [start];
    visited[start] = 1;

    while (order.length < count) {
        const current = order.at(-1);
        let closest = -1;
        let best = Infinity;
        for (let point = 0; point < count; point++) {
            if (visited[point]) continue;

            const candidate = squaredDistance(current, point);
            if (candidate < best) {
                closest = point;
                best = candidate;
            }
        }
        visited[closest] = 1;
        order.push(closest);
    }
    return order;
}

function improveWithTwoOpt(order, loop, distance) {
    const count = order.length;
    const stride = Math.ceil(count / 200);
    const passes = count <= 200 ? 20 : 8;

    for (let pass = 0; pass < passes; pass++) {
        let bestDelta = -epsilon;
        let bestStart = -1;
        let bestEnd = -1;
        for (let start = loop ? 1 : 0; start < count - 1; start++) {
            const before = order[start - 1];
            const first = order[start];
            const oldLeft = start === 0 ? 0 : distance(before, first);

            // Short reversals get a full scan. Larger routes sample longer reversals.
            for (let end = start + 1; end < count;) {
                const last = order[end];
                const after = end + 1 < count ? order[end + 1] : loop ? order[0] : undefined;
                const left = start === 0 ? 0 : distance(before, last) - oldLeft;
                const right = after === undefined ? 0 : distance(first, after) - distance(last, after);
                const delta = left + right;
                if (delta < bestDelta) {
                    bestDelta = delta;
                    bestStart = start;
                    bestEnd = end;
                }

                if (end === count - 1) break;
                end = Math.min(count - 1, end + (end - start < 12 ? 1 : stride));
            }
        }
        if (bestStart < 0) return;

        while (bestStart < bestEnd) {
            [order[bestStart], order[bestEnd]] = [order[bestEnd], order[bestStart]];
            bestStart++;
            bestEnd--;
        }
    }
}
