export class RecentOrder {
  constructor({ now = () => Date.now() } = {}) { this.now = now; }
  wait = null;

  arrange(target) { return target; }
}
