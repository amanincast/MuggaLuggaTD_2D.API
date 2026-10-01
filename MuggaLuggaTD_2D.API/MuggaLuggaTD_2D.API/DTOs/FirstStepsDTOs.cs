using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>A player's First Steps on one world: the steps done and whether the chest is open.</summary>
public record FirstStepsResponse(List<string> Done, bool ChestOpened);

/// <summary>One of the steps a client may report itself (opening the Hall, putting on gear).</summary>
public record FirstStepsMarkRequest(string Step);

/// <summary>The chest's piece of gear, for the client to add to its inventory.</summary>
public record FirstStepsChestResponse(ItemSaveData Item, List<string> Done);
