using System.Net;
using System.Net.Sockets;
using MotorBench.Modbus;

namespace MotorBench.Tests;

public class ModbusPduTests
{
    [Fact]
    public void Read_request_is_big_endian()
    {
        var pdu = ModbusPdu.ReadRequest(ModbusPdu.ReadHoldingRegisters, 1024, 6);
        Assert.Equal(new byte[] { 0x03, 0x04, 0x00, 0x00, 0x06 }, pdu);
    }

    [Fact]
    public void Write_multiple_registers_layout()
    {
        var pdu = ModbusPdu.WriteMultipleRegistersRequest(1025, [1500, 50]);
        Assert.Equal(new byte[] { 0x10, 0x04, 0x01, 0x00, 0x02, 0x04, 0x05, 0xDC, 0x00, 0x32 }, pdu);
    }

    [Fact]
    public void Coils_are_unpacked_lsb_first()
    {
        var response = new byte[] { 0x01, 0x02, 0b0000_1101, 0b0000_0010 };
        var bits = ModbusPdu.ParseCoils(response, 10);
        Assert.Equal(new[] { true, false, true, true, false, false, false, false, false, true }, bits);
    }

    [Fact]
    public void Exception_response_throws_with_code()
    {
        var ex = Assert.Throws<ModbusException>(() => ModbusPdu.ThrowIfException(0x03, new byte[] { 0x83, 0x02 }));
        Assert.Equal(ModbusPdu.IllegalDataAddress, ex.ExceptionCode);
    }

    [Fact]
    public void Wrong_length_is_rejected()
    {
        Assert.Throws<IOException>(() => ModbusPdu.ParseRegisters(new byte[] { 0x03, 0x04, 0x00, 0x01 }, 2));
    }

    [Fact]
    public void Bank_rejects_bad_address_and_unknown_function()
    {
        var bank = new RegisterBank(holdingCount: 16, coilCount: 8);

        var response = bank.Handle(ModbusPdu.ReadRequest(ModbusPdu.ReadHoldingRegisters, 10, 10));
        Assert.Equal(new byte[] { 0x83, ModbusPdu.IllegalDataAddress }, response);

        response = bank.Handle(new byte[] { 0x2B, 0, 0, 0, 0 });
        Assert.Equal(new byte[] { 0xAB, ModbusPdu.IllegalFunction }, response);
    }

    [Theory]
    [InlineData(new byte[] { }, new byte[] { 0x80, ModbusPdu.IllegalFunction })]
    [InlineData(new byte[] { 0x03, 0x00 }, new byte[] { 0x83, ModbusPdu.IllegalDataValue })] // truncated
    [InlineData(new byte[] { 0x05, 0x00, 0x01, 0x12, 0x34 }, new byte[] { 0x85, ModbusPdu.IllegalDataValue })] // coil value not ON/OFF
    [InlineData(new byte[] { 0x05, 0x00, 0x08, 0xFF, 0x00 }, new byte[] { 0x85, ModbusPdu.IllegalDataAddress })] // coil 8 of 8
    [InlineData(new byte[] { 0x06, 0x00, 0x10, 0x00, 0x01 }, new byte[] { 0x86, ModbusPdu.IllegalDataAddress })] // register 16 of 16
    public void Bank_answers_malformed_requests_with_an_exception(byte[] request, byte[] expected) =>
        Assert.Equal(expected, new RegisterBank(holdingCount: 16, coilCount: 8).Handle(request));
}

public class ModbusTcpTests
{
    [Fact]
    public async Task Write_then_read_back()
    {
        var bank = new RegisterBank();
        await using var server = new ModbusTcpServer(bank, IPAddress.Loopback, 0);
        server.Start();
        using var client = new ModbusTcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        await client.WriteMultipleRegistersAsync(1024, [7, 1500, 65535]);
        await client.WriteSingleRegisterAsync(1028, 42);
        var regs = await client.ReadHoldingRegistersAsync(1024, 5);
        Assert.Equal(new ushort[] { 7, 1500, 65535, 0, 42 }, regs);

        bank.SetCoil(1, true);
        bank.SetCoil(3, true);
        var coils = await client.ReadCoilsAsync(0, 4);
        Assert.Equal(new[] { false, true, false, true }, coils);
    }

    [Fact]
    public async Task Exception_response_keeps_the_connection()
    {
        var bank = new RegisterBank();
        await using var server = new ModbusTcpServer(bank, IPAddress.Loopback, 0);
        server.Start();
        using var client = new ModbusTcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var ex = await Assert.ThrowsAsync<ModbusException>(() => client.ReadHoldingRegistersAsync(2040, 20));
        Assert.Equal(ModbusPdu.IllegalDataAddress, ex.ExceptionCode);

        Assert.True(client.IsConnected);
        Assert.Equal(new ushort[] { 0 }, await client.ReadHoldingRegistersAsync(0, 1));
    }

    // A reply that is not clearly the answer to this request is never used, and the
    // connection is dropped so the next request starts clean.
    [Theory]
    [InlineData("wrong-transaction")]
    [InlineData("bad-protocol")]
    [InlineData("peer-closes")]
    public async Task Bad_reply_is_rejected_and_drops_the_connection(string fault)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new ModbusTcpClient { Timeout = TimeSpan.FromSeconds(2) };
            await client.ConnectAsync("127.0.0.1", port);
            var peer = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync();
                var stream = tcp.GetStream();
                var request = new byte[12]; // MBAP + read-holding PDU
                await stream.ReadExactlyAsync(request);
                if (fault == "peer-closes")
                    return;
                byte tidLo = fault == "wrong-transaction" ? (byte)(request[1] + 1) : request[1];
                byte protocol = fault == "bad-protocol" ? (byte)1 : (byte)0;
                // otherwise a valid reply: one register = 7
                await stream.WriteAsync(new byte[] { request[0], tidLo, 0, protocol, 0, 5, request[6], 0x03, 0x02, 0x00, 0x07 });
            });

            await Assert.ThrowsAnyAsync<IOException>(() => client.ReadHoldingRegistersAsync(0, 1));
            Assert.False(client.IsConnected);
            await peer;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Timeout_drops_the_connection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new ModbusTcpClient { Timeout = TimeSpan.FromMilliseconds(200) };
            await client.ConnectAsync("127.0.0.1", port);
            using var silent = await listener.AcceptTcpClientAsync();

            await Assert.ThrowsAsync<TimeoutException>(() => client.ReadHoldingRegistersAsync(0, 1));
            Assert.False(client.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }
}
