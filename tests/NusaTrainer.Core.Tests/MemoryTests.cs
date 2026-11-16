using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Trainer;

namespace NusaTrainer.Core.Tests;

public class AobPatternTests
{
    [Fact]
    public void Parses_wildcards()
    {
        var p = AobPattern.Parse("48 8B ?? 05 ? 90");
        Assert.Equal(6, p.Length);
        Assert.Equal([true, true, false, true, false, true], p.Mask);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?? ??")]
    [InlineData("4G")]
    [InlineData("123")]
    public void Rejects_invalid(string pattern) => Assert.Throws<FormatException>(() => AobPattern.Parse(pattern));

    [Fact]
    public void Finds_match_with_wildcards_and_skips_partial_matches()
    {
        byte[] data = [0x00, 0x48, 0x8B, 0x01, 0x48, 0x8B, 0xFF, 0x05, 0x11, 0x90, 0x00];
        var p = AobPattern.Parse("48 8B ?? 05 ?? 90");
        Assert.Equal(4, p.IndexOf(data));
    }

    [Fact]
    public void Leading_wildcard_supported()
    {
        byte[] data = [0x00, 0x01, 0x02, 0xAA, 0xBB];
        Assert.Equal(1, AobPattern.Parse("?? ?? AA BB").IndexOf(data));
    }

    [Fact]
    public void Scanner_respects_region_filter()
    {
        var mem = new FakeMemory();
        mem.Put(0x10, [0xDE, 0xAD, 0xBE, 0xEF]);   // di region kode
        mem.Put(0x2000, [0xDE, 0xAD, 0xBE, 0xEF]); // di region data
        var p = AobPattern.Parse("DE AD BE EF");
        Assert.Equal(FakeMemory.Base + 0x10, MemoryScanner.FindFirst(mem, p, "code"));
        Assert.Equal(FakeMemory.Base + 0x2000, MemoryScanner.FindFirst(mem, p, "data"));
        Assert.Equal(FakeMemory.Base + 0x10, MemoryScanner.FindFirst(mem, p, "any"));
    }
}

public class AddressResolverTests
{
    [Fact]
    public void Follows_pointer_chain_like_cheat_engine()
    {
        var mem = new FakeMemory();
        // [game.exe+0x100] -> Base+0x2000 ; [Base+0x2000+0x10] -> Base+0x3000 ; final = Base+0x3000+0x8
        mem.PutLong(0x100, (long)(FakeMemory.Base + 0x2000));
        mem.PutLong(0x2010, (long)(FakeMemory.Base + 0x3000));
        var r = new AddressResolver(mem);
        var addr = r.Resolve(new AddressSpec { Module = "game.exe", Offset = "0x100", Pointers = ["0x10", "0x8"] });
        Assert.Equal(FakeMemory.Base + 0x3008, addr);
    }

    [Fact]
    public void Null_pointer_gives_friendly_error()
    {
        var mem = new FakeMemory();
        var r = new AddressResolver(mem);
        var ex = Assert.Throws<MemoryAccessException>(() =>
            r.Resolve(new AddressSpec { Module = "game.exe", Offset = "0x100", Pointers = ["0x10"] }));
        Assert.Contains("null", ex.Message);
    }

    [Fact]
    public void Resolves_rip_relative_instruction()
    {
        var mem = new FakeMemory();
        // mov rax, [rip+disp32]  = 48 8B 05 <disp32>   di offset 0x40 (kode)
        int instr = 0x40;
        int target = 0x2500;
        int disp = target - (instr + 7);
        mem.Put(instr, [0x48, 0x8B, 0x05]);
        mem.PutInt(instr + 3, disp);
        mem.Put(instr + 7, [0x89, 0x88]);
        var r = new AddressResolver(mem);
        var addr = r.Resolve(new AddressSpec
        {
            Pattern = "48 8B 05 ?? ?? ?? ?? 89 88", Region = "code",
            Rip = new RipRelativeSpec { InstructionOffset = 0, DisplacementOffset = 3, InstructionLength = 7 },
        });
        Assert.Equal(FakeMemory.Base + (ulong)target, addr);
    }

    [Theory]
    [InlineData("0x1F", 31)]
    [InlineData("1f", 31)]
    [InlineData("-0x10", -16)]
    public void Parses_hex(string s, long expected) => Assert.Equal(expected, AddressResolver.ParseHex(s));
}
