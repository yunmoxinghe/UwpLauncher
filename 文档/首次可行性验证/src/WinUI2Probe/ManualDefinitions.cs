using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Windows.Win32
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate LRESULT WNDPROC([In] HWND hWnd, [In] uint uMsg, [In] WPARAM wParam, [In] LPARAM lParam);
}
