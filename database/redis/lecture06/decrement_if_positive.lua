local stored = redis.call('GET', KEYS[1])
if not stored then
    return redis.error_reply('availability unknown')
end

local count = tonumber(stored)
if count == nil or count < 0 or count % 1 ~= 0 then
    return redis.error_reply('invalid availability')
end

if count == 0 then
    return 0
end

return redis.call('DECR', KEYS[1])
