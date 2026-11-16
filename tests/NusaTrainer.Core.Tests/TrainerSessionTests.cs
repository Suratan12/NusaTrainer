using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Trainer;

namespace NusaTrainer.Core.Tests;

public class TrainerSessionTests
{
    private static readonly byte[] Magic = "NUSA-DEMO-GAME!!"u8.ToArray();
    private const string MagicPattern = "4E 55 53 41 2D 44 45 4D 4F 2D 47 41 4D 45 21 21";
    private const int Block = 0x3000;

    private static AddressSpec At(int offset) => new() { Pattern = MagicPattern, Region = "data", PatternOffset = offset };

    private static (FakeMemory mem, TrainerSession session) Create(params CheatDefinition[] cheats)
    {
        var mem = new FakeMemory();
        mem.Put(Block, Magic);
        mem.PutInt(Block + 16, 37);   // health
        mem.PutInt(Block + 24, 50);   // gold
        var game = new GameDefinition { Id = "t", Name = "T", ProcessNames = ["t"], Cheats = cheats.ToList() };
        // Interval timer sangat panjang: Tick() dipanggil manual agar test deterministik.
        return (mem, new TrainerSession(game, mem, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Freeze_toggle_writes_on_enable_and_each_tick()
    {
        var (mem, s) = Create(new CheatDefinition { Id = "hp", Type = CheatKind.Toggle, Address = At(16), Value = 100 });
        s.SetToggle("hp", true);
        Assert.Equal(100, mem.GetInt(Block + 16));
        mem.PutInt(Block + 16, 3); // game mengurangi nyawa
        s.Tick();
        Assert.Equal(100, mem.GetInt(Block + 16));
        s.SetToggle("hp", false);
        mem.PutInt(Block + 16, 3);
        s.Tick();
        Assert.Equal(3, mem.GetInt(Block + 16));
    }

    [Fact]
    public void Button_add_adds_to_current_value()
    {
        var (mem, s) = Create(new CheatDefinition
            { Id = "gold", Type = CheatKind.Button, Operation = CheatOperation.Add, Address = At(24), Value = 1000 });
        s.Press("gold");
        s.Press("gold");
        Assert.Equal(2050, mem.GetInt(Block + 24));
    }

    [Fact]
    public void Slider_set_writes_clamped_value_immediately()
    {
        var (mem, s) = Create(new CheatDefinition
        {
            Id = "g", Type = CheatKind.Slider, Operation = CheatOperation.Set, Address = At(24), Min = 0, Max = 500,
        });
        s.SetSliderValue("g", 9999);
        Assert.Equal(500, mem.GetInt(Block + 24));
    }

    [Fact]
    public void Slider_freeze_only_writes_when_locked()
    {
        var (mem, s) = Create(new CheatDefinition
        {
            Id = "spd", Type = CheatKind.Slider, Operation = CheatOperation.Freeze, ValueType = DataType.Float,
            Address = At(28), Min = 0.5, Max = 5, Step = 0.5, Default = 2,
        });
        s.SetSliderValue("spd", 3);
        Assert.Equal(0f, mem.GetFloat(Block + 28));
        s.SetToggle("spd", true);
        Assert.Equal(3f, mem.GetFloat(Block + 28));
        s.SetSliderValue("spd", 4.5);
        Assert.Equal(4.5f, mem.GetFloat(Block + 28));
    }

    [Fact]
    public void Toggle_set_with_restore_returns_original_value()
    {
        var (mem, s) = Create(new CheatDefinition
        {
            Id = "hp", Type = CheatKind.Toggle, Operation = CheatOperation.Set, RestoreOnDisable = true, Address = At(16), Value = 999,
        });
        s.SetToggle("hp", true);
        Assert.Equal(999, mem.GetInt(Block + 16));
        s.SetToggle("hp", false);
        Assert.Equal(37, mem.GetInt(Block + 16));
    }

    [Fact]
    public void Code_patch_applies_and_reverts()
    {
        var cheat = new CheatDefinition
        {
            Id = "nodmg", Type = CheatKind.Toggle,
            Patch = new PatchSpec
            {
                Address = new AddressSpec { Pattern = "29 47 10 C3", Region = "code" },
                Bytes = "90 90 90", ExpectedOriginal = "29 47 10",
            },
        };
        var (mem, s) = Create(cheat);
        mem.Put(0x80, [0x29, 0x47, 0x10, 0xC3]); // sub [rdi+10], eax ; ret
        s.SetToggle("nodmg", true);
        Assert.Equal(new byte[] { 0x90, 0x90, 0x90, 0xC3 }, mem.Data[0x80..0x84]);
        s.SetToggle("nodmg", false);
        Assert.Equal(new byte[] { 0x29, 0x47, 0x10, 0xC3 }, mem.Data[0x80..0x84]);
    }

    [Fact]
    public void Code_patch_refuses_when_original_bytes_differ()
    {
        var cheat = new CheatDefinition
        {
            Id = "nodmg", Type = CheatKind.Toggle,
            Patch = new PatchSpec
            {
                Address = new AddressSpec { Pattern = "29 47 ?? C3", Region = "code" },
                Bytes = "90 90 90", ExpectedOriginal = "29 47 10",
            },
        };
        var (mem, s) = Create(cheat);
        mem.Put(0x80, [0x29, 0x47, 0x18, 0xC3]);
        Assert.Throws<MemoryAccessException>(() => s.SetToggle("nodmg", true));
        Assert.False(s.IsEnabled("nodmg"));
        Assert.Equal(new byte[] { 0x29, 0x47, 0x18, 0xC3 }, mem.Data[0x80..0x84]);
    }

    [Fact]
    public void Dispose_reverts_patches()
    {
        var cheat = new CheatDefinition
        {
            Id = "p", Type = CheatKind.Toggle,
            Patch = new PatchSpec { Address = new AddressSpec { Pattern = "AB CD EF", Region = "code" }, Bytes = "90 90 90" },
        };
        var (mem, s) = Create(cheat);
        mem.Put(0x90, [0xAB, 0xCD, 0xEF]);
        s.SetToggle("p", true);
        s.Dispose();
        Assert.Equal(new byte[] { 0xAB, 0xCD, 0xEF }, mem.Data[0x90..0x93]);
    }

    [Fact]
    public void Failing_freeze_disables_cheat_and_raises_event()
    {
        var mem = new FakeMemory();
        mem.PutLong(0x100, (long)(FakeMemory.Base + 0x2000));
        var game = new GameDefinition
        {
            Id = "t", Name = "T", ProcessNames = ["t"],
            Cheats = [new CheatDefinition { Id = "hp", Address = new AddressSpec { Module = "game.exe", Offset = "0x100", Pointers = ["0x8"] }, Value = 1 }],
        };
        using var s = new TrainerSession(game, mem, TimeSpan.FromHours(1));
        s.SetToggle("hp", true);
        string? failed = null;
        s.CheatFailed += (id, _) => failed = id;
        mem.PutLong(0x100, 0); // pointer hilang (mis. kembali ke menu)
        s.Tick();
        Assert.Equal("hp", failed);
        Assert.False(s.IsEnabled("hp"));
    }

    [Theory]
    [InlineData(DataType.Byte, 300, 255)]
    [InlineData(DataType.Int16, -5, -5)]
    [InlineData(DataType.Int64, 1e12, 1e12)]
    [InlineData(DataType.Double, 1.25, 1.25)]
    public void ValueCodec_roundtrip(DataType t, double input, double expected) =>
        Assert.Equal(expected, ValueCodec.Decode(t, ValueCodec.Encode(t, input)));
}
