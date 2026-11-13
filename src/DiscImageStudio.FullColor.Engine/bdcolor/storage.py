"""Atomic local-file commits, Windows sharing retries, and one output owner."""
from contextlib import contextmanager
import errno
import json
import os
from pathlib import Path
import time
import uuid


_RETRY_SECONDS=4.0


def retry_file_operation(operation,path):
    deadline=time.monotonic()+_RETRY_SECONDS
    delay=.025
    while True:
        try: return operation()
        except OSError as error:
            transient=(getattr(error,'winerror',None) in (5,32,33)
                       or error.errno in (errno.EACCES,errno.EBUSY,errno.EAGAIN))
            if not transient: raise
            if time.monotonic()>=deadline:
                failure=PermissionError(errno.EACCES,
                    'File is busy or access is denied. Close the application using it and retry; '
                    'export data and recovery records have been retained.',str(path))
                raise failure from error
            time.sleep(min(delay,max(0,deadline-time.monotonic())))
            delay=min(delay*2,.5)


def atomic_json(path,value):
    target=Path(path)
    temporary=target.with_name('.bdcolor-'+uuid.uuid4().hex+'.tmp')
    try:
        with temporary.open('x',encoding='utf-8',newline='\n') as stream:
            json.dump(value,stream,ensure_ascii=False,indent=2);stream.write('\n')
            stream.flush();os.fsync(stream.fileno())
        retry_file_operation(lambda:os.replace(temporary,target),target)
    finally:
        try: temporary.unlink(missing_ok=True)
        except OSError: pass


def publish_file(source,target,overwrite):
    source,target=Path(source),Path(target)
    if overwrite:
        retry_file_operation(lambda:os.replace(source,target),target)
    elif os.name=='nt':
        # Windows rename refuses an existing destination; never silently grant
        # overwrite permission if another application creates the path meanwhile.
        retry_file_operation(lambda:os.rename(source,target),target)
    else:
        retry_file_operation(lambda:os.link(source,target),target)
        source.unlink()


def image_identity(path):
    stat=Path(path).stat()
    return {'device':stat.st_dev,'inode':stat.st_ino}


@contextmanager
def output_lock(path):
    target=Path(path);target.parent.mkdir(parents=True,exist_ok=True)
    # Leave the tiny lock file in place. Unlinking it would allow a waiting
    # process and a new process to lock different inodes for the same output.
    lock_path=target.with_name(target.name+'.export.lock')
    with lock_path.open('a+b') as stream:
        stream.seek(0,2)
        if stream.tell()==0: stream.write(b'\0');stream.flush()
        stream.seek(0)
        try:
            if os.name=='nt':
                import msvcrt
                msvcrt.locking(stream.fileno(),msvcrt.LK_NBLCK,1)
            else:
                import fcntl
                fcntl.flock(stream.fileno(),fcntl.LOCK_EX|fcntl.LOCK_NB)
        except OSError as error:
            raise FileExistsError(errno.EBUSY,'Another export owns this output path. Wait for it to finish or choose another name.',str(target)) from error
        try: yield
        finally:
            stream.seek(0)
            if os.name=='nt': msvcrt.locking(stream.fileno(),msvcrt.LK_UNLCK,1)
            else: fcntl.flock(stream.fileno(),fcntl.LOCK_UN)
