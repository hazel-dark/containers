using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;
using Wdk = Windows.Wdk.PInvoke;

namespace Containers.Windows.Silos;

/// <summary>A handle to a Windows job object, including a job configured as a silo.</summary>
[SupportedOSPlatform("windows")]
public class JobSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an empty handle for native interop.</summary>
    public JobSafeHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps a native job handle. Set <paramref name="ownsHandle"/> to close it on disposal.</summary>
    public JobSafeHandle(nint handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);

    /// <summary>Holds a reference to an existing SafeHandle known to represent a job.
    /// Disposing the returned handle releases that reference.</summary>
    public static JobSafeHandle Borrow(SafeHandle job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new BorrowedJob(job);
    }

    protected override bool ReleaseHandle() => Wdk.NtClose((HANDLE)handle) >= 0;

    private sealed class BorrowedJob : JobSafeHandle
    {
        private readonly SafeHandle _source;

        internal BorrowedJob(SafeHandle source)
        {
            _source = source;
            var borrowed = false;
            try
            {
                source.DangerousAddRef(ref borrowed);
                var value = source.DangerousGetHandle();
                if (source.IsInvalid || value is 0 or -1)
                    throw new ArgumentException("A valid job handle is required.", nameof(source));

                SetHandle(value);
            }
            catch
            {
                if (borrowed)
                    source.DangerousRelease();
                throw;
            }
        }

        protected override bool ReleaseHandle()
        {
            _source.DangerousRelease();
            return true;
        }
    }
}
