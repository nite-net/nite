// Just enough of Node's built-in modules for PostCSS, Tailwind and Less to run inside Jint.
// File access goes through __host (NodeLiteHost in C#). Paths always use forward slashes, also on
// Windows ("C:/app/x.css"), which both sides accept.
(function () {
  "use strict";
  const host = __host;

  const isAbsolute = (p) => p.startsWith("/") || /^[A-Za-z]:\//.test(p);
  const rootOf = (p) => (p.startsWith("/") ? "/" : /^[A-Za-z]:\//.test(p) ? p.slice(0, 3) : "");
  const normalize = (input) => {
    let p = String(input).replace(/\\/g, "/");
    const root = rootOf(p);
    const out = [];
    for (const part of p.slice(root.length).split("/")) {
      if (!part || part === ".") continue;
      if (part === "..") {
        if (out.length && out[out.length - 1] !== "..") out.pop();
        else if (!root) out.push("..");
      } else out.push(part);
    }
    const joined = root + out.join("/");
    return joined || (root ? root : ".");
  };
  const path = {
    sep: "/",
    delimiter: host.isWindows() ? ";" : ":",
    normalize,
    isAbsolute,
    join: (...parts) => normalize(parts.filter((p) => p !== "").join("/") || "."),
    resolve: (...parts) => {
      let resolved = "";
      for (let i = parts.length - 1; i >= 0 && !isAbsolute(resolved); i--) {
        if (parts[i]) resolved = String(parts[i]).replace(/\\/g, "/") + (resolved ? "/" + resolved : "");
      }
      if (!isAbsolute(resolved)) resolved = host.cwd() + (resolved ? "/" + resolved : "");
      return normalize(resolved);
    },
    dirname: (p) => {
      p = String(p).replace(/\\/g, "/");
      const root = rootOf(p);
      const trimmed = p.length > root.length ? p.replace(/\/+$/, "") : p;
      const i = trimmed.lastIndexOf("/");
      if (i < root.length) return root || ".";
      return trimmed.slice(0, i) || root;
    },
    basename: (p, ext) => {
      let base = String(p).replace(/\\/g, "/").replace(/\/+$/, "");
      base = base.slice(base.lastIndexOf("/") + 1);
      if (ext && base.endsWith(ext) && base !== ext) base = base.slice(0, -ext.length);
      return base;
    },
    extname: (p) => {
      const base = path.basename(p);
      const i = base.lastIndexOf(".");
      return i <= 0 ? "" : base.slice(i);
    },
    relative: (from, to) => {
      const a = path.resolve(from).split("/").filter(Boolean);
      const b = path.resolve(to).split("/").filter(Boolean);
      let i = 0;
      while (i < a.length && i < b.length && a[i] === b[i]) i++;
      return [...Array(a.length - i).fill(".."), ...b.slice(i)].join("/");
    },
    parse: (p) => {
      const base = path.basename(p);
      const ext = path.extname(p);
      return { root: rootOf(String(p)), dir: path.dirname(p), base, ext, name: ext ? base.slice(0, -ext.length) : base };
    },
    format: (o) => (o.dir ? o.dir + "/" : "") + (o.base || (o.name || "") + (o.ext || "")),
    toNamespacedPath: (p) => p,
  };
  path.posix = path;
  path.win32 = path;

  const notFound = (p, syscall) => {
    const error = new Error(`ENOENT: no such file or directory, ${syscall} '${p}'`);
    error.code = "ENOENT";
    error.errno = -2;
    error.syscall = syscall;
    error.path = p;
    return error;
  };
  const asPath = (p) => {
    p = typeof p === "object" && p !== null && "href" in p ? url.fileURLToPath(p) : String(p);
    return path.resolve(p);
  };
  const makeStats = (p, kind, size, mtimeMs) => ({
    isFile: () => kind === "file",
    isDirectory: () => kind === "dir",
    isSymbolicLink: () => false,
    isFIFO: () => false,
    isSocket: () => false,
    isBlockDevice: () => false,
    isCharacterDevice: () => false,
    size,
    mtimeMs,
    mtime: new Date(mtimeMs),
    ctimeMs: mtimeMs,
    ctime: new Date(mtimeMs),
    birthtimeMs: mtimeMs,
    mode: kind === "dir" ? 0o40755 : 0o100644,
  });
  const statSync = (p, options) => {
    const file = asPath(p);
    const info = host.stat(file); // "file|size|mtime", "dir|0|mtime" or ""
    if (!info) {
      if (options && options.throwIfNoEntry === false) return undefined;
      throw notFound(file, "stat");
    }
    const [kind, size, mtime] = info.split("|");
    return makeStats(file, kind, Number(size), Number(mtime));
  };
  const readFileSync = (p, options) => {
    const file = asPath(p);
    if (!host.isFile(file)) throw notFound(file, "open");
    return host.readFile(file);
  };
  const readdirSync = (p, options) => {
    const dir = asPath(p);
    if (!host.isDirectory(dir)) throw notFound(dir, "scandir");
    const names = host.readDirectory(dir).split("\n").filter(Boolean);
    if (options && options.withFileTypes) {
      return names.map((entry) => {
        const kind = entry[0];
        const name = entry.slice(2);
        return {
          name,
          parentPath: dir,
          path: dir,
          isFile: () => kind === "f",
          isDirectory: () => kind === "d",
          isSymbolicLink: () => false,
        };
      });
    }
    return names.map((entry) => entry.slice(2));
  };
  const realpathSync = (p) => {
    const file = asPath(p);
    if (!host.stat(file)) throw notFound(file, "realpath");
    return file;
  };
  realpathSync.native = realpathSync;
  const later = (fn) => Promise.resolve().then(fn);
  const callbackify = (sync) => (...args) => {
    const callback = args.pop();
    later(() => {
      let result;
      try {
        result = sync(...args);
      } catch (error) {
        callback(error);
        return;
      }
      callback(null, result);
    });
  };
  const promisified = (sync) => (...args) => new Promise((resolve, reject) => {
    try {
      resolve(sync(...args));
    } catch (error) {
      reject(error);
    }
  });
  const accessSync = (p) => {
    statSync(p);
  };
  const readOnly = (name) => () => {
    throw new Error(`fs.${name} is not available to build tools running inside Nite.`);
  };
  const fs = {
    constants: { F_OK: 0, R_OK: 4, W_OK: 2, X_OK: 1 },
    existsSync: (p) => {
      try {
        return !!host.stat(asPath(p));
      } catch {
        return false;
      }
    },
    statSync,
    lstatSync: statSync,
    readFileSync,
    readdirSync,
    realpathSync,
    accessSync,
    writeFileSync: readOnly("writeFileSync"),
    mkdirSync: readOnly("mkdirSync"),
    watch: () => ({ close() {}, on() { return this; } }),
  };
  fs.stat = callbackify(statSync);
  fs.lstat = fs.stat;
  fs.readFile = callbackify(readFileSync);
  fs.readdir = callbackify(readdirSync);
  fs.realpath = callbackify(realpathSync);
  fs.realpath.native = fs.realpath;
  fs.access = callbackify(accessSync);
  fs.promises = {
    stat: promisified(statSync),
    lstat: promisified(statSync),
    readFile: promisified(readFileSync),
    readdir: promisified(readdirSync),
    realpath: promisified(realpathSync),
    access: promisified(accessSync),
  };

  class NiteURL {
    constructor(input, base) {
      let href = String(input);
      if (base !== undefined && !/^[a-z][a-z0-9+.-]*:/i.test(href)) {
        const baseHref = String(base);
        href = baseHref.slice(0, baseHref.lastIndexOf("/") + 1) + href;
      }
      const match = /^([a-z][a-z0-9+.-]*:)(\/\/[^/?#]*)?([^?#]*)(\?[^#]*)?(#.*)?$/i.exec(href);
      if (!match) throw new TypeError(`Invalid URL: ${input}`);
      this.protocol = match[1].toLowerCase();
      this.host = (match[2] || "").slice(2);
      this.hostname = this.host.replace(/:\d+$/, "");
      this.pathname = match[3] ? normalize(match[3]).replace(/^\.$/, "/") : "/";
      if (match[3] && match[3].endsWith("/") && !this.pathname.endsWith("/")) this.pathname += "/";
      this.search = match[4] || "";
      this.hash = match[5] || "";
      this.origin = this.protocol === "file:" ? "null" : this.protocol + "//" + this.host;
    }
    get href() {
      return this.protocol + (this.host || this.protocol === "file:" ? "//" + this.host : "") + this.pathname + this.search + this.hash;
    }
    toString() {
      return this.href;
    }
    toJSON() {
      return this.href;
    }
  }
  const url = {
    URL: NiteURL,
    fileURLToPath: (u) => {
      const href = typeof u === "string" ? u : u.href;
      if (!href.startsWith("file:")) throw new TypeError("The URL must be of scheme file");
      let p = decodeURIComponent(href.replace(/^file:\/\/[^/]*/, ""));
      if (/^\/[A-Za-z]:\//.test(p)) p = p.slice(1);
      return p;
    },
    pathToFileURL: (p) => new NiteURL("file://" + (path.resolve(p).startsWith("/") ? "" : "/") + encodeURI(path.resolve(p))),
  };
  if (typeof globalThis.URL === "undefined") globalThis.URL = NiteURL;

  const inspect = (value) => {
    try {
      return typeof value === "string" ? value : JSON.stringify(value);
    } catch {
      return String(value);
    }
  };
  const format = (first, ...rest) => {
    if (typeof first !== "string") return [first, ...rest].map(inspect).join(" ");
    let i = 0;
    const text = first.replace(/%[sdifjoO%]/g, (token) => (token === "%%" ? "%" : i < rest.length ? inspect(rest[i++]) : token));
    return [text, ...rest.slice(i).map(inspect)].join(" ");
  };
  const util = {
    format,
    inspect,
    deprecate: (fn) => fn,
    inherits: (child, parent) => {
      Object.setPrototypeOf(child.prototype, parent.prototype);
      Object.setPrototypeOf(child, parent);
    },
    promisify: (fn) => (...args) => new Promise((resolve, reject) => fn(...args, (error, value) => (error ? reject(error) : resolve(value)))),
    isDeepStrictEqual: (a, b) => JSON.stringify(a) === JSON.stringify(b),
    types: { isPromise: (v) => v instanceof Promise, isRegExp: (v) => v instanceof RegExp, isDate: (v) => v instanceof Date },
    TextEncoder: globalThis.TextEncoder,
    TextDecoder: globalThis.TextDecoder,
  };

  class EventEmitter {
    constructor() {
      this._events = {};
    }
    on(name, fn) {
      (this._events[name] = this._events[name] || []).push(fn);
      return this;
    }
    once(name, fn) {
      const wrapped = (...args) => {
        this.off(name, wrapped);
        fn(...args);
      };
      return this.on(name, wrapped);
    }
    off(name, fn) {
      this._events[name] = (this._events[name] || []).filter((f) => f !== fn);
      return this;
    }
    emit(name, ...args) {
      for (const fn of this._events[name] || []) fn(...args);
      return (this._events[name] || []).length > 0;
    }
  }
  EventEmitter.prototype.addListener = EventEmitter.prototype.on;
  EventEmitter.prototype.removeListener = EventEmitter.prototype.off;
  EventEmitter.EventEmitter = EventEmitter;

  const log = (level) => (...args) => host.log(level, format(...args));
  globalThis.console = { log: log("info"), info: log("info"), debug: log("debug"), warn: log("warn"), error: log("error"), trace: log("debug") };

  const timers = {
    setTimeout: (fn, ms, ...args) => {
      later(() => fn(...args));
      return 0;
    },
    clearTimeout: () => {},
    setImmediate: (fn, ...args) => {
      later(() => fn(...args));
      return 0;
    },
    clearImmediate: () => {},
  };
  Object.assign(globalThis, timers);
  globalThis.setInterval = () => 0;
  globalThis.clearInterval = () => {};
  if (typeof globalThis.queueMicrotask === "undefined") globalThis.queueMicrotask = (fn) => later(fn);
  if (typeof globalThis.structuredClone === "undefined") {
    globalThis.structuredClone = function clone(value, seen = new Map()) {
      if (value === null || typeof value !== "object") return value;
      if (seen.has(value)) return seen.get(value);
      let copy;
      if (value instanceof Date) return new Date(value.getTime());
      if (value instanceof RegExp) return new RegExp(value.source, value.flags);
      if (value instanceof Map) {
        copy = new Map();
        seen.set(value, copy);
        for (const [k, v] of value) copy.set(clone(k, seen), clone(v, seen));
        return copy;
      }
      if (value instanceof Set) {
        copy = new Set();
        seen.set(value, copy);
        for (const v of value) copy.add(clone(v, seen));
        return copy;
      }
      copy = Array.isArray(value) ? [] : {};
      seen.set(value, copy);
      for (const key of Object.keys(value)) copy[key] = clone(value[key], seen);
      return copy;
    };
  }
  if (typeof globalThis.performance === "undefined") globalThis.performance = { now: () => Date.now() };

  const env = new Proxy({}, {
    get: (_, key) => (typeof key === "string" ? host.env(key) ?? undefined : undefined),
    has: (_, key) => typeof key === "string" && host.env(key) != null,
    set: () => true,
  });
  globalThis.process = {
    env,
    argv: ["node", "nite"],
    platform: host.isWindows() ? "win32" : "linux",
    version: "v22.0.0",
    versions: { node: "22.0.0" },
    cwd: () => host.cwd(),
    nextTick: (fn, ...args) => later(() => fn(...args)),
    hrtime: Object.assign((previous) => {
      const now = Date.now();
      const time = [Math.floor(now / 1000), (now % 1000) * 1e6];
      return previous ? [time[0] - previous[0], time[1] - previous[1]] : time;
    }, { bigint: () => BigInt(Date.now()) * 1000000n }),
    emitWarning: () => {},
    on() { return this; },
    once() { return this; },
    exit: () => {},
    stdout: { isTTY: false, write: (text) => host.log("info", String(text)) },
    stderr: { isTTY: false, write: (text) => host.log("warn", String(text)) },
  };

  const os = {
    EOL: "\n",
    platform: () => process.platform,
    homedir: () => host.env("HOME") ?? host.env("USERPROFILE") ?? "/",
    tmpdir: () => "/tmp",
    cpus: () => [{}],
    type: () => "Nite",
  };

  const builtins = {
    fs,
    "fs/promises": fs.promises,
    path,
    "path/posix": path,
    url,
    util,
    events: EventEmitter,
    os,
    process: globalThis.process,
    timers,
    module: { createRequire: () => globalThis.require, builtinModules: [] },
    assert: Object.assign((value, message) => {
      if (!value) throw new Error(message || "Assertion failed");
    }, { ok: (value, message) => { if (!value) throw new Error(message || "Assertion failed"); } }),
    tty: { isatty: () => false },
    v8: {},
    vm: {},
    crypto: {},
    worker_threads: { isMainThread: true },
  };
  globalThis.require = (name) => {
    const id = String(name).replace(/^node:/, "");
    if (Object.prototype.hasOwnProperty.call(builtins, id)) return builtins[id];
    const error = new Error(`Cannot find module '${name}' (only bundled packages and Node built-ins are available inside Nite)`);
    error.code = "MODULE_NOT_FOUND";
    throw error;
  };
  globalThis.require.resolve = (name) => {
    throw Object.assign(new Error(`Cannot resolve '${name}' inside Nite`), { code: "MODULE_NOT_FOUND" });
  };
  // Bundled ESM tools read import.meta.url and import.meta.dirname; point them at the project.
  globalThis.__nite_import_meta_url = url.pathToFileURL(host.cwd() + "/index.js").href;
  globalThis.__nite_import_meta_dirname = host.cwd();
})();
