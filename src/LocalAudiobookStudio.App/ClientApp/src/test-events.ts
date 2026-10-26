// Browser transport double; all application components and HTTP decoding stay real.
export class TestEventSource extends EventTarget {
  static instances: TestEventSource[] = [];
  closed = false;
  constructor(public url: string) { super(); TestEventSource.instances.push(this); }
  close() { this.closed = true; }
  emit(type: string) { this.dispatchEvent(new MessageEvent(type, { data: '{}' })); }
}
