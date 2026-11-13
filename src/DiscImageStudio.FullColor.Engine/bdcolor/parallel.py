"""Bounded CPU scheduling; workers never own the output file or checkpoint."""
from collections import deque
from concurrent.futures import ThreadPoolExecutor, ProcessPoolExecutor, wait, FIRST_COMPLETED
import multiprocessing
import os
import time


def available_cpus():
    """Respect process affinity, including on Python 3.11/Windows."""
    process_count = getattr(os, 'process_cpu_count', None)
    if process_count is not None:
        return max(1, process_count() or 1)
    try:
        return max(1, len(os.sched_getaffinity(0)))
    except (AttributeError,OSError):
        pass
    if os.name == 'nt':
        try:
            import ctypes
            from ctypes import wintypes
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.GetCurrentProcess.restype = wintypes.HANDLE
            query = kernel.GetProcessAffinityMask
            query.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_size_t), ctypes.POINTER(ctypes.c_size_t)]
            query.restype = wintypes.BOOL
            process_mask, system_mask = ctypes.c_size_t(), ctypes.c_size_t()
            if query(kernel.GetCurrentProcess(), ctypes.byref(process_mask), ctypes.byref(system_mask)) and process_mask.value:
                return process_mask.value.bit_count()
        except (OSError, AttributeError):
            pass
    return max(1, os.cpu_count() or 1)


def available_memory():
    """Available physical memory; zero means the platform could not report it."""
    if os.name=='nt':
        try:
            import ctypes
            class MemoryStatus(ctypes.Structure):
                _fields_=[('length',ctypes.c_uint32),('load',ctypes.c_uint32)]+[
                    (name,ctypes.c_uint64) for name in ('total','available','totalPage','availablePage',
                                                       'totalVirtual','availableVirtual','extended')]
            status=MemoryStatus();status.length=ctypes.sizeof(status)
            query=ctypes.WinDLL('kernel32',use_last_error=True).GlobalMemoryStatusEx
            query.argtypes=[ctypes.POINTER(MemoryStatus)];query.restype=ctypes.c_int
            if query(ctypes.byref(status)): return int(status.available)
        except (OSError,AttributeError): pass
    else:
        try: return os.sysconf('SC_AVPHYS_PAGES')*os.sysconf('SC_PAGE_SIZE')
        except (OSError,ValueError,AttributeError): pass
    return 0


def enable_all_cpus():
    """Auto mode may use the full system mask, only in this calculation process.

    Python/BLAS launch policies can inherit a P-core-only mask on hybrid Windows
    CPUs. Never change a global setting; job-object restrictions remain enforced
    by Windows, and failure falls back to the currently permitted process mask.
    """
    if os.name=='nt':
        try:
            import ctypes
            from ctypes import wintypes
            kernel=ctypes.WinDLL('kernel32',use_last_error=True)
            kernel.GetCurrentProcess.restype=wintypes.HANDLE
            handle=kernel.GetCurrentProcess()
            process_mask,system_mask=ctypes.c_size_t(),ctypes.c_size_t()
            query=kernel.GetProcessAffinityMask
            query.argtypes=[wintypes.HANDLE,ctypes.POINTER(ctypes.c_size_t),ctypes.POINTER(ctypes.c_size_t)]
            query.restype=wintypes.BOOL
            update=kernel.SetProcessAffinityMask
            update.argtypes=[wintypes.HANDLE,ctypes.c_size_t];update.restype=wintypes.BOOL
            if query(handle,ctypes.byref(process_mask),ctypes.byref(system_mask)) and system_mask.value:
                if process_mask.value!=system_mask.value: update(handle,system_mask.value)
        except (OSError,AttributeError): pass
    return available_cpus()


def requested_workers(requested=None):
    value = os.environ.get('DIS_FULLCOLOR_WORKERS', '0') if requested is None else requested
    try:
        count = int(value)
        if float(value) != count or not 0 <= count <= 1024:
            raise ValueError()
    except (ValueError, TypeError, OverflowError) as exc:
        raise ValueError('CPU workers must be an integer from 0 (automatic) to 1024.') from exc
    return count


def worker_count(requested=None):
    count = requested_workers(requested)
    available = available_cpus()
    return min(count, available) if count else available


def adaptive_threads(function, sample_batches, maximum, sample_count, log=print):
    """Probe up to ALL available CPUs; select throughput, not a fixed cap."""
    counts = [1]
    while counts[-1] * 2 < maximum:
        counts.append(counts[-1] * 2)
    if maximum not in counts: counts.append(maximum)
    measurements = []
    for count in counts:
        started = time.perf_counter()
        results = map(function, sample_batches) if count == 1 else ordered_map(function, sample_batches, count)
        for result in results:
            del result
        seconds = time.perf_counter() - started
        rate = sample_count / max(seconds, 1e-9)
        measurements.append({'workers':count,'seconds':seconds,'clustersPerSecond':rate})
        log(f'CPU probe: {count}/{maximum} threads, {rate:,.0f} clusters/s.')
    # Prefer fewer workers within 3% of the best result; no throughput gain is
    # worth extra memory contention on hybrid/SMT processors.
    best_rate = max(m['clustersPerSecond'] for m in measurements)
    selected = next(m['workers'] for m in measurements if m['clustersPerSecond'] >= best_rate*.97)
    return selected, measurements


def ordered_map(function, items, workers, *, processes=False, initializer=None, initargs=()):
    """At most 2*workers pending batches, returned in the original input order.

    Python 3.11 Executor.map submits the whole iterable eagerly. Keep an explicit
    window instead, so memory stays bounded even for a complete BD image.
    """
    arguments = dict(max_workers=workers, initializer=initializer, initargs=initargs)
    if processes:
        # Explicit spawn is also safe when tests run under a threaded WPF host.
        pool = ProcessPoolExecutor(mp_context=multiprocessing.get_context('spawn'), **arguments)
    else:
        pool = ThreadPoolExecutor(**arguments)
    pending = deque()
    iterator = iter(items)
    try:
        for _ in range(2 * workers):
            try: item = next(iterator)
            except StopIteration: break
            pending.append(pool.submit(function, item))
        while pending:
            result = pending.popleft().result()
            yield result
            try: item = next(iterator)
            except StopIteration: continue
            pending.append(pool.submit(function, item))
    finally:
        for future in pending: future.cancel()
        pool.shutdown(wait=True, cancel_futures=True)


def completed_map(function, items, workers, *, initializer=None, initargs=()):
    """Color rows are disjoint: collect finished rows without head-of-line waits."""
    pool = ProcessPoolExecutor(max_workers=workers, mp_context=multiprocessing.get_context('spawn'),
                               initializer=initializer, initargs=initargs)
    pending = set()
    iterator = iter(items)
    try:
        for _ in range(2*workers):
            try: item = next(iterator)
            except StopIteration: break
            pending.add(pool.submit(function,item))
        while pending:
            done,pending = wait(pending,return_when=FIRST_COMPLETED)
            for future in done:
                result = future.result()
                try: item = next(iterator)
                except StopIteration: pass
                else: pending.add(pool.submit(function,item))
                yield result
    finally:
        for future in pending: future.cancel()
        pool.shutdown(wait=True,cancel_futures=True)
