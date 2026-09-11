# MUD2 flee cost

Source: private email from Richard Bartle to the operator, quoted verbatim below. Received before
2026-09-11 (the date it was placed here). This is tier-1 evidence: the game's author reading the
game's source. It outranks every estimate derived from captures.

> OK, well looking at the code (which I'm not entirely sure is all mine), when you flee you lose
> 2/9 of your fleeworth.
>
> To calculate your fleeworth, take your stamina as a percentage of your maximum stamina. If this
> percentage is < 6, your fleeworth is 0.
>
> Otherwise, it's your worth times 4 divided by:
>
> - If your stamina/max stamina as a % is 100 then 1
> - If it's 76 to 99 then 2
> - If it's 16 to 75 then 4
> - If it's less than 16 then 8
>
> To calculate your worth, that's 75 plus (your points plus bounties on you)/5.
>
> I haven't checked any of this by plugging in numbers, so it could well be wrong.
>
> Richard

## In the code

`mudsharp/Combat/FleeWorth.cs` implements the formula as written. `FleeWorthTests.RecordedFlights`
checks it against flights recorded in the operator's own sessions; that is the only verification
that exists, and Bartle's own caveat stands.
