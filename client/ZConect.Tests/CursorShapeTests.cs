using UiApp.Services;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for CursorShapeService.MapResourceId — cursor wResID to string name mapping.</summary>
public sealed class CursorShapeTests
{
    [Theory]
    [InlineData((ushort)32512, "arrow")]      // IDC_ARROW
    [InlineData((ushort)32513, "ibeam")]       // IDC_IBEAM
    [InlineData((ushort)32649, "hand")]        // IDC_HAND
    [InlineData((ushort)32514, "wait")]        // IDC_WAIT
    [InlineData((ushort)32644, "sizewe")]      // IDC_SIZEWE
    [InlineData((ushort)32648, "no")]          // IDC_NO
    [InlineData((ushort)32650, "appstarting")] // IDC_APPSTARTING
    [InlineData((ushort)32515, "cross")]       // IDC_CROSS
    [InlineData((ushort)32645, "sizens")]      // IDC_SIZENS
    [InlineData((ushort)32642, "sizenwse")]    // IDC_SIZENWSE
    [InlineData((ushort)32643, "sizenesw")]    // IDC_SIZENESW
    [InlineData((ushort)32646, "sizeall")]     // IDC_SIZEALL
    [InlineData((ushort)32651, "help")]        // IDC_HELP
    [InlineData((ushort)32516, "uparrow")]     // IDC_UPARROW
    public void MapResourceId_KnownCursors(ushort wResID, string expected)
    {
        Assert.Equal(expected, CursorShapeService.MapResourceId(wResID));
    }

    [Theory]
    [InlineData((ushort)99)]     // random unknown value
    [InlineData((ushort)0)]      // zero
    [InlineData((ushort)65535)]  // max ushort
    public void MapResourceId_UnknownId_ReturnsArrow(ushort wResID)
    {
        Assert.Equal("arrow", CursorShapeService.MapResourceId(wResID));
    }
}
